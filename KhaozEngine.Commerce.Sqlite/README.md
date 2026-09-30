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
no-op that returns the prior balance when its signed amount and `LedgerReason` match. A different amount, reason,
or direction returns `Conflict=true` and leaves the balance and ledger unchanged. The same key on a different
account, or a different currency on the same account, is a distinct operation. The existing ledger row already
holds the signed delta and reason, so this needs no extra fingerprint table or schema migration.

Every row time is Unix milliseconds. `wallet_ledger.created_at` is each append's time. A `wallet_balance` row
carries `created_at` beside the `updated_at` every credit and debit moves, and a `grant_schedule` row carries
`created_at` and an `updated_at` that moves only when a write changes the stored instant. Construction adds any of
those three columns a table an older build created lacks, as nullable columns in one immediate transaction, and the
rows already there keep NULL where no write since knows the time.

Construction reads the required tables, columns and ledger indexes first. A complete schema takes no write lock,
so the store can open while another connection holds a write transaction. Missing requirements are rechecked under
the immediate transaction before any DDL runs. Missing ledger indexes alone are repaired through the same path,
including the unique index that enforces idempotency.

Opt-in: pulls `Microsoft.Data.Sqlite` without touching the dependency-free `KhaozEngine.Commerce` core. Not
bundled in the `Server` umbrella. Dispose the store to close the connection. The connection is never pooled, so the OS
handle on the database file is genuinely released on dispose rather than parked in the provider's pool, and the
file can be deleted, rotated or exclusively opened straight after (since 17.41.0).

The connection, the operation gate and that dispose are `KhaozEngine.Sqlite`'s `SqliteStoreConnection`, shared
with every other SQLite store in the engine. `SqliteSchemaWidening` coordinates the schema check and locked recheck.
Only the schema and the SQL live here.
