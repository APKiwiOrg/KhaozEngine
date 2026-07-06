# KhaozEngine.Commerce

Server-authoritative currency wallet: atomic idempotent ledger with materialized balance, source-agnostic entitlement pipeline, and server-clock periodic grants.

## Overview

Provides a foundational wallet system with:
- Identity-agnostic account keying (verified AccountId)
- Multi-currency support (CurrencyId)
- Immutable ledger entries with idempotency keys
- Credit and debit operations with result types
- Zero external dependencies (no SQL, no netcode)

## Quick Start

```csharp
using KhaozEngine.Commerce;

var accountId = new AccountId("player:1234");
var currencyId = new CurrencyId("shard");

// Create a wallet with your store
var wallet = new Wallet(store);

// Credit currency
var creditResult = wallet.Credit(accountId, currencyId, 100,
    idempotencyKey: "grant:daily:2026-07-07",
    reason: LedgerReason.Grant);

// Debit currency
var debitResult = wallet.Debit(accountId, currencyId, 50,
    idempotencyKey: "spend:upgrade:tower",
    reason: LedgerReason.Spend);
```

See `docs/COMMERCE.md` for full API reference and store backends.
