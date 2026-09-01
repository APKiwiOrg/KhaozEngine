# KhaozEngine.Commerce.SqlServer

SQL Server / Azure SQL backend for `KhaozEngine.Commerce` (`IWalletStore` + `IGrantScheduleStore`) over
`Microsoft.Data.SqlClient`. The production durable wallet store; same contract as the SQLite dev/test backend.

```csharp
using KhaozEngine.Commerce;
using KhaozEngine.Commerce.SqlServer;

IWalletStore store = new SqlServerWalletStore(
    "Server=tcp:my.database.windows.net,1433;Database=ruinborne;Authentication=Active Directory Default;Encrypt=True;");
CreditResult r = await store.CreditAsync(new AccountId("acct:1"), new CurrencyId("shard"),
    100, "grant:daily:2026-07-07", LedgerReason.Grant, sourceRef: null);
```

Schema (`wallet_ledger`, `wallet_balance`, `grant_schedule`) is bootstrapped on construction via
`IF OBJECT_ID(...) IS NULL` guards. Each credit/debit opens a fresh pooled `SqlConnection` and runs inside a
`SqlTransaction` at `IsolationLevel.Serializable`; the database serializes concurrent operations, there is no
in-process semaphore. Idempotency is enforced by a composite unique index on
`(account_id, currency_id, idempotency_key)`: replaying an already-seen key for the same account and currency is a
no-op that returns the prior balance; the same key on a different account, or a different currency on the same
account, is a distinct operation. Credit upserts the balance with a relative
`UPDATE ... SET amount = amount + @amt ... OUTPUT inserted.amount`, falling back to an `INSERT` when that update
matched no row, so a first-ever credit for an account and currency creates the balance row. Debit uses a
conditional `UPDATE ... WHERE amount >= @amt` and checks `@@ROWCOUNT` to reject an overspend atomically, writing no
ledger row. The only `MERGE ... WITH (HOLDLOCK)` in the store is in `SetNextAvailableAsync`, on `grant_schedule`,
not on either wallet path. A duplicate-key race on the ledger insert (`SqlException` 2601/2627) is treated as a replay: the prior
`post_balance` for that composite key is re-read and returned.

Opt-in: pulls `Microsoft.Data.SqlClient` without touching the dependency-free `KhaozEngine.Commerce` core. Not
bundled in the `Server` umbrella.

## Key columns are case sensitive by collation

Account ids, currency ids, idempotency keys and reward ids are compared by code point, matching the InMemory
backend (ordinal string equality) and the SQLite one (`TEXT`, BINARY collation). The tables this store creates
declare `COLLATE Latin1_General_100_BIN2` on those columns rather than inheriting the database default, which on
most SQL Server and Azure SQL installs is case-insensitive (`SQL_Latin1_General_CP1_CI_AS` and friends).

That default is not a cosmetic difference. Under it, `"claim-ABC"` and `"claim-abc"` are the same row in
`ux_ledger_idem`, so the second call is answered as a replay of the first and its credit or debit is silently
swallowed, while the same two calls apply on the other two backends. Account ids collide the same way in
`wallet_balance`'s primary key, giving two accounts one shared wallet.

Two consequences worth knowing. A query of your own that joins one of these columns against a column in the
database's default collation now raises a collation conflict (error 468) instead of comparing, and needs an
explicit `COLLATE` on the comparison. And this governs tables this store CREATES: the `IF OBJECT_ID(...) IS NULL`
guards leave an already-deployed table exactly as it was, so a database that predates this pin keeps its old
collation until migrated.

### Migrating an existing database

Indexed columns cannot be altered in place, so drop the indexes and the primary keys, alter, then put them back.
Run it in a maintenance window with the game's servers stopped. It cannot fail on duplicate keys: the old
case-insensitive index was strictly stricter than the new one, so every row that exists is still unique under
code-point comparison.

```sql
-- 1. wallet_ledger: two plain indexes, no constraints to hunt for.
DROP INDEX ux_ledger_idem ON dbo.wallet_ledger;
DROP INDEX ix_ledger_acct ON dbo.wallet_ledger;
ALTER TABLE dbo.wallet_ledger ALTER COLUMN account_id      NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL;
ALTER TABLE dbo.wallet_ledger ALTER COLUMN currency_id     NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL;
ALTER TABLE dbo.wallet_ledger ALTER COLUMN idempotency_key NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL;
CREATE UNIQUE INDEX ux_ledger_idem ON dbo.wallet_ledger(account_id, currency_id, idempotency_key);
CREATE INDEX ix_ledger_acct ON dbo.wallet_ledger(account_id, currency_id, id DESC);

-- 2. wallet_balance and grant_schedule: the primary keys were auto-named, so drop them by lookup.
DECLARE @sql NVARCHAR(MAX) = N'';
SELECT @sql = @sql + N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name)
             + N' DROP CONSTRAINT ' + QUOTENAME(k.name) + N';'
FROM sys.key_constraints k JOIN sys.tables t ON t.object_id = k.parent_object_id
WHERE k.type = 'PK' AND t.name IN (N'wallet_balance', N'grant_schedule');
EXEC sp_executesql @sql;

ALTER TABLE dbo.wallet_balance  ALTER COLUMN account_id  NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL;
ALTER TABLE dbo.wallet_balance  ALTER COLUMN currency_id NVARCHAR(100) COLLATE Latin1_General_100_BIN2 NOT NULL;
ALTER TABLE dbo.grant_schedule  ALTER COLUMN account_id  NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL;
ALTER TABLE dbo.grant_schedule  ALTER COLUMN reward_id   NVARCHAR(200) COLLATE Latin1_General_100_BIN2 NOT NULL;

ALTER TABLE dbo.wallet_balance ADD PRIMARY KEY (account_id, currency_id);
ALTER TABLE dbo.grant_schedule ADD PRIMARY KEY (account_id, reward_id);
```

Leaving a deployed database unmigrated is a choice you can make, as long as you make it knowingly: that database
keeps case-insensitive keys, and the wallet behaves differently there than it does on the other backends and on
any database created after this pin.

## Before first real-money use

Run the gated tests (`KE_COMMERCE_SQLSERVER=<conn> dotnet test ...`) against a real (Azure) SQL instance before
trusting this store with real money. They are skipped, not run, in a normal local/CI pass. This exercises the
composite unique index, the atomic-update paths for both credit and debit, and the 2601/2627 duplicate-key replay
recovery under an actual server, and is the point to watch for 1205 deadlocks under parallel load.

Point the case-sensitivity row at a database whose DEFAULT collation is case-insensitive, which is the
deployment the pinned column collation exists to protect. Against a database that was already created
case-sensitive, that row passes either way and proves nothing.
