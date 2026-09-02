using System.Threading.Tasks;
using KhaozEngine.Commerce;
using KhaozEngine.Commerce.Sqlite;
using KhaozEngine.Tests.Sqlite;

namespace KhaozEngine.Tests.Commerce;

/// <summary>
/// The wallet store's half of the shared release contract (see <see cref="SqliteStoreFileLifetimeContract{TStore}"/>
/// for what it proves and why both of its assertions are needed). This is #715, the second copy of the same leak,
/// and the store now inherits the fix from <c>SqliteStoreConnection</c> rather than carrying it.
///
/// <para>The rest of the SQLite wallet suite runs on <c>Mode=Memory;Cache=Shared</c>, so there is no file for it to
/// fail to release. That is why this defect shipped, and why this class insists on a temp file.</para>
/// </summary>
public sealed class SqliteWalletStoreFileLifetimeTests : SqliteStoreFileLifetimeContract<SqliteWalletStore>
{
    private static readonly AccountId Account = new("acct:1");
    private static readonly CurrencyId Currency = new("shard");

    protected override string FileStem => "ke-wallet-life-";

    protected override SqliteWalletStore Open(string connectionString) => new(connectionString);

    protected override Task WriteAsync(SqliteWalletStore store)
        => store.CreditAsync(Account, Currency, 5, "k1", LedgerReason.Grant, null);

    protected override async Task<bool> HasTheWriteAsync(SqliteWalletStore store)
        => await store.GetBalanceAsync(Account, Currency) != 0;
}
