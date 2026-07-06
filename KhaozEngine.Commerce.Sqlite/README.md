# KhaozEngine.Commerce.Sqlite

SQLite backend for `KhaozEngine.Commerce` (`IWalletStore` + `IGrantScheduleStore`) over `Microsoft.Data.Sqlite`.
The embedded, zero-infra dev/test and single-node durable wallet store.

```csharp
using KhaozEngine.Commerce;
using KhaozEngine.Commerce.Sqlite;

IWalletStore store = new SqliteWalletStore("Data Source=wallet.db");
CreditResult r = await store.CreditAsync(new AccountId("acct:1"), new CurrencyId("shard"),
    100, "grant:daily:2026-07-07", LedgerReason.Grant, sourceRef: null);
```

Schema (`wallet_ledger`, `wallet_balance`, `grant_schedule`) is bootstrapped on construction. Credit/debit run
inside a SQLite transaction over a single held connection, serialized by a semaphore so operations never overlap
on the shared connection. Idempotency is enforced by a composite unique index on
`(account_id, currency_id, idempotency_key)`: replaying an already-seen key for the same account and currency is a
no-op that returns the prior balance; the same key on a different account, or a different currency on the same
account, is a distinct operation.

Opt-in: pulls `Microsoft.Data.Sqlite` without touching the dependency-free `KhaozEngine.Commerce` core. Not
bundled in the `Server` umbrella. Dispose the store to close the connection.
