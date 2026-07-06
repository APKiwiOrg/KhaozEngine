using System;
using System.Threading;
using System.Threading.Tasks;

namespace KhaozEngine.Commerce;

/// <summary>Server-authoritative currency operations over an <see cref="IWalletStore"/>. All money in
/// and out flows through one idempotent path.</summary>
public sealed class Wallet
{
    private readonly IWalletStore store;
    private readonly IProductCatalog catalog;

    public Wallet(IWalletStore store, IProductCatalog catalog)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    /// <summary>A server-authorized free grant (e.g. daily). Idempotent by idempotency key.</summary>
    public Task<CreditResult> GrantAsync(AccountId account, CurrencyId currency, long amount,
        string idempotencyKey, CancellationToken ct = default)
        => store.CreditAsync(account, currency, amount, idempotencyKey, LedgerReason.Grant, null, ct);

    /// <summary>A player spend. Idempotent; returns <c>Insufficient</c> if the balance is too low.</summary>
    public Task<DebitResult> SpendAsync(AccountId account, CurrencyId currency, long amount,
        string idempotencyKey, CancellationToken ct = default)
        => store.DebitAsync(account, currency, amount, idempotencyKey, LedgerReason.Spend, null, ct);

    public Task<long> BalanceAsync(AccountId account, CurrencyId currency, CancellationToken ct = default)
        => store.GetBalanceAsync(account, currency, ct);

    /// <summary>Credit a validated purchase. Idempotent by the entitlement's source transaction id.</summary>
    /// <exception cref="ArgumentException">The product id is not in the catalog, or quantity is not positive.</exception>
    public Task<CreditResult> RedeemAsync(VerifiedEntitlement ent, CancellationToken ct = default)
    {
        if (ent.Quantity <= 0)
            throw new ArgumentException("Quantity must be positive.", nameof(ent));
        if (!catalog.TryGet(ent.ProductId, out ProductDefinition def))
            throw new ArgumentException($"Unknown product '{ent.ProductId}'.", nameof(ent));
        long amount = def.AmountPerUnit * ent.Quantity;
        return store.CreditAsync(ent.Account, def.Currency, amount,
            idempotencyKey: $"src:{ent.SourceTxnId}", LedgerReason.Purchase, ent.SourceTxnId, ct);
    }
}
