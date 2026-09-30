# KhaozEngine.Commerce

Server-authoritative currency wallet: atomic idempotent ledger with materialized balance, source-agnostic entitlement pipeline, and server-clock periodic grants.

## Overview

Provides a foundational wallet system with:
- Identity-agnostic account keying (`AccountId`, a verified account key the consumer supplies)
- Multi-currency support (`CurrencyId`)
- An `IWalletStore` seam (durable, atomic, idempotent credit/debit + immutable ledger) with an `InMemoryWalletStore` reference/test backend; durable SQL backends are the opt-in `KhaozEngine.Commerce.Sqlite` / `KhaozEngine.Commerce.SqlServer` sibling packages
- `Wallet`: grant (free credit), spend (debit), and redeem (purchase entitlement) over the store
- A source-agnostic entitlement pipeline: `VerifiedEntitlement`, `EntitlementProof`, `IEntitlementValidator` (no default implementation; the proof format and trust decision are the consumer's)
- `IProductCatalog` / `InMemoryProductCatalog`: maps a store product id to the currency + amount it grants
- `PeriodicGrant` + `IGrantScheduleStore`: a server-clock daily/periodic reward routed through the wallet, built on `KhaozEngine.Progression`'s `WallClockRewardSchedule`. The first-ever claim per `(account, reward)` is a permanent one-shot keyed in the wallet ledger, so never clear the schedule store while retaining the wallet ledger. Use `PeriodicGrant.ResetAsync` to re-open a reward (a wiped-progress account, a seasonal re-issue on the same reward id): it writes a schedule row rather than deleting one, which keeps the bootstrap path unreachable
- Zero external dependencies (no SQL, no netcode)

## Public API

| Type | What it does |
|---|---|
| `AccountId`, `CurrencyId` | Opaque, non-empty string identifiers (`IEquatable`, ordinal comparison). |
| `IWalletStore` | `CreditAsync`/`DebitAsync` (atomic, idempotent by `idempotencyKey`, scoped per account+currency, with explicit intent conflicts), `GetBalanceAsync`, `GetLedgerAsync`. Keys compare by code point on every backend, so `"claim-ABC"` is not a replay of `"claim-abc"`. |
| `InMemoryWalletStore` | In-process reference/test `IWalletStore` + `IGrantScheduleStore`, single lock for atomicity. |
| `Wallet` | `GrantAsync` (free credit), `SpendAsync` (debit), `RedeemAsync` (credit a `VerifiedEntitlement` via the product catalog), `BalanceAsync`. |
| `LedgerEntry` | Immutable ledger row: `Delta` (negative = debit, positive = credit), `Reason`, `SourceRef`, `IdempotencyKey`, `CreatedAt`. |
| `LedgerReason` | Why a row exists: `Grant`, `Purchase`, `Spend`, `Adjustment`. It does not affect balance math, but it participates in idempotency conflict detection. |
| `CreditResult` / `DebitResult` | `Applied`, `Replayed` (the same idempotent intent was already applied), `Conflict` (the key belongs to a different intent), `Insufficient` (debit only), `NewBalance`. |
| `IProductCatalog`, `InMemoryProductCatalog`, `ProductDefinition` | Maps a product id to `(CurrencyId, AmountPerUnit)`. |
| `VerifiedEntitlement`, `EntitlementProof`, `IEntitlementValidator` | Turns an untrusted external proof into a verified, account-resolved entitlement (or null). |
| `IGrantScheduleStore` | Persists the next-available instant per `(account, rewardId)` for `PeriodicGrant`. |
| `PeriodicGrant`, `PeriodicGrantResult` | Server-clock daily/periodic reward: `TryClaimAsync(account, serverNowUtc)`, plus `ResetAsync(account, availableFromUtc)` to re-open it. The `rewardId` may not contain `':'` (the idempotency key's segment separator), while the account id still may. |

An idempotency key binds the signed amount and `LedgerReason`. An exact retry returns `Replayed=true` and the
original operation's historical post-balance. Reusing the key with another amount or reason, including switching
between credit and debit, returns `Conflict=true` and leaves the balance and ledger unchanged. A conflict carries
the same historical post-balance. `SourceRef` is provenance only and does not take part in the comparison.

## Quick Start

```csharp
using KhaozEngine.Commerce;

var account = new AccountId("player:1234");
var currency = new CurrencyId("shard");

// A store: InMemoryWalletStore for tests/dev, or a durable backend
// (KhaozEngine.Commerce.Sqlite / KhaozEngine.Commerce.SqlServer).
InMemoryWalletStore store = new();
var catalog = new InMemoryProductCatalog(new[]
{
    new ProductDefinition(ProductId: "shards_100", Currency: currency, AmountPerUnit: 100),
});
var wallet = new Wallet(store, catalog);

// A server-authorized free grant.
CreditResult granted = await wallet.GrantAsync(account, currency, 50,
    idempotencyKey: "grant:daily:2026-07-07");

// A player spend.
DebitResult spent = await wallet.SpendAsync(account, currency, 20,
    idempotencyKey: "spend:upgrade:tower");

// A validated purchase, idempotent by the entitlement's source transaction id.
var entitlement = new VerifiedEntitlement(account, ProductId: "shards_100", SourceTxnId: "txn_abc123", Quantity: 1);
CreditResult redeemed = await wallet.RedeemAsync(entitlement);

long balance = await wallet.BalanceAsync(account, currency);
```

See [`docs/USING-KHAOZENGINE.md`](../docs/USING-KHAOZENGINE.md) ("Commerce / wallet") for the full consumer
walkthrough including `PeriodicGrant`, and [`docs/DEPENDENCY-SEAMS.md`](../docs/DEPENDENCY-SEAMS.md)
("Commerce wallet seams") for the seam/backend split. Durable backends: `KhaozEngine.Commerce.Sqlite`,
`KhaozEngine.Commerce.SqlServer`.
