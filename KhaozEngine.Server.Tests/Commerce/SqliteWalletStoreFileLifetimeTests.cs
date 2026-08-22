using System;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.Commerce;
using KhaozEngine.Commerce.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Commerce;

/// <summary>
/// A disposed store holds no OS handle on its database file. Microsoft.Data.Sqlite pools connections by default,
/// so <c>SqliteConnection.Dispose()</c> hands the native handle back to the pool instead of closing it, and the
/// file stays open for as long as the pool holds it. That leak reads differently per platform and is a defect on
/// all of them: Windows refuses to delete or exclusively open a file something still has open, while POSIX unlinks
/// it happily and then hands the SAME leaked handle to the next store opened on that path, which quietly serves
/// the deleted database. A server rotating or dropping a wallet file hits one or the other depending on where it
/// runs. This is #715, the same defect fixed in <c>SqliteWorldStore</c> as #713.
///
/// Both assertions below earn their place, because neither one covers every platform on its own: the exclusive
/// open is what bites on Windows and is vacuous on POSIX (.NET implements FileShare with flock, SQLite locks with
/// fcntl, and the two never collide), and the delete-then-reopen is what bites on POSIX.
///
/// The rest of the SQLite wallet suite runs on <c>Mode=Memory;Cache=Shared</c>, so there is no file for it to fail
/// to release. That is why this defect shipped, and why this class insists on a temp file.
/// </summary>
public sealed class SqliteWalletStoreFileLifetimeTests
{
    private static readonly AccountId Account = new("acct:1");
    private static readonly CurrencyId Currency = new("shard");

    private static string TempDb()
        => Path.Combine(Path.GetTempPath(), "ke-wallet-life-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public async Task A_disposed_store_leaves_its_file_closed_and_deletable()
    {
        string path = TempDb();
        try
        {
            using (var store = new SqliteWalletStore($"Data Source={path}"))
                await store.CreditAsync(Account, Currency, 5, "k1", LedgerReason.Grant, null);

            // Windows: throws IOException while any handle on the file is open.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            // POSIX: the delete above the reopen always succeeds, so the delete alone proves nothing. What a
            // leaked handle cannot survive is the reopen: it is still bound to the unlinked inode, so the pool
            // hands it to the fresh store below and the 5 shards come back out of a database that no longer
            // exists.
            File.Delete(path);
            using var reopened = new SqliteWalletStore($"Data Source={path}");
            Assert.Equal(0, await reopened.GetBalanceAsync(Account, Currency));
        }
        finally { File.Delete(path); }
    }
}
