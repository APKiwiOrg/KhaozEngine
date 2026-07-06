using System.Threading.Tasks;
using KhaozEngine.Commerce;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

public class WalletTests
{
    private static readonly AccountId A = new("acct:1");
    private static readonly CurrencyId Shard = new("shard");

    private static Wallet NewWallet(out IWalletStore store)
    {
        store = new InMemoryWalletStore();
        IProductCatalog catalog = new InMemoryProductCatalog(new[]
        {
            new ProductDefinition("shardpack.small", Shard, 100),
        });
        return new Wallet(store, catalog);
    }

    [Fact]
    public async Task Redeem_credits_amount_times_quantity_idempotently()
    {
        Wallet w = NewWallet(out _);
        VerifiedEntitlement ent = new(A, "shardpack.small", "txn:abc", 2);
        CreditResult r1 = await w.RedeemAsync(ent);
        CreditResult r2 = await w.RedeemAsync(ent); // replay same SourceTxnId
        Assert.Equal(200, r1.NewBalance);
        Assert.True(r2.Replayed);
        Assert.Equal(200, await w.BalanceAsync(A, Shard));
    }

    [Fact]
    public async Task Redeem_unknown_product_throws()
    {
        Wallet w = NewWallet(out _);
        await Assert.ThrowsAsync<System.ArgumentException>(
            () => w.RedeemAsync(new VerifiedEntitlement(A, "nope", "t", 1)));
    }

    [Fact]
    public async Task Spend_reduces_balance_and_blocks_overspend()
    {
        Wallet w = NewWallet(out _);
        await w.GrantAsync(A, Shard, 50, "seed");
        DebitResult ok = await w.SpendAsync(A, Shard, 20, "buy1");
        Assert.Equal(30, ok.NewBalance);
        DebitResult over = await w.SpendAsync(A, Shard, 999, "buy2");
        Assert.True(over.Insufficient);
        Assert.Equal(30, await w.BalanceAsync(A, Shard));
    }
}
