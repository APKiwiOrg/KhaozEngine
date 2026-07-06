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
account, is a distinct operation. Credit upserts the balance via `MERGE ... WITH (HOLDLOCK)`; debit uses a
conditional `UPDATE ... WHERE amount >= @amt` and checks `@@ROWCOUNT` to reject an overspend atomically, writing no
ledger row. A duplicate-key race on the ledger insert (`SqlException` 2601/2627) is treated as a replay: the prior
`post_balance` for that composite key is re-read and returned.

Opt-in: pulls `Microsoft.Data.SqlClient` without touching the dependency-free `KhaozEngine.Commerce` core. Not
bundled in the `Server` umbrella.

## Before first real-money use

Run the gated tests (`KE_COMMERCE_SQLSERVER=<conn> dotnet test ...`) against a real (Azure) SQL instance before
trusting this store with real money. They are skipped, not run, in a normal local/CI pass. This exercises the
composite unique index, the atomic-update paths for both credit and debit, and the 2601/2627 duplicate-key replay
recovery under an actual server, and is the point to watch for 1205 deadlocks under parallel load.
