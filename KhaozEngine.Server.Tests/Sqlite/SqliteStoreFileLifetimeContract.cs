using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace KhaozEngine.Tests.Sqlite;

/// <summary>
/// The release test shape every SQLite-backed store owes its database file: a disposed store holds no OS handle on
/// it. Microsoft.Data.Sqlite pools connections by default, so <c>SqliteConnection.Dispose()</c> hands the native
/// handle back to the pool instead of closing it, and the file stays open for as long as the pool holds it. That
/// leak reads differently per platform and is a defect on all of them: Windows refuses to delete or exclusively
/// open a file something still has open (#713, which took both Windows GPU legs down on an unrelated branch), while
/// POSIX unlinks it happily and then hands the SAME leaked handle to the next store opened on that path, which
/// quietly serves the deleted database. A server rotating or dropping a store file hits one or the other depending
/// on where it runs.
///
/// <para>Both assertions below earn their place, because neither one covers every platform on its own: the
/// exclusive open is what bites on Windows and is vacuous on POSIX (.NET implements FileShare with flock, SQLite
/// locks with fcntl, and the two never collide), and the delete-then-reopen is what bites on POSIX.</para>
///
/// <para>It is a shared base rather than a copied method because the discipline it pins was copied and then shipped
/// broken three times: <c>SqliteWorldStore</c> (#713), <c>SqliteWalletStore</c> (#715) and a consumer's own accounts
/// store. A store built on <c>KhaozEngine.Sqlite.SqliteStoreConnection</c> inherits the fix, and deriving here is
/// how it proves it.</para>
/// </summary>
/// <typeparam name="TStore">The store under test, opened from an ADO.NET connection string.</typeparam>
public abstract class SqliteStoreFileLifetimeContract<TStore> where TStore : IDisposable
{
    /// <summary>Opens the store on <paramref name="connectionString"/>, bootstrapping whatever schema it needs.</summary>
    protected abstract TStore Open(string connectionString);

    /// <summary>Writes one record, so the file has content a leaked handle could go on serving after the delete.</summary>
    protected abstract Task WriteAsync(TStore store);

    /// <summary>Whether <paramref name="store"/> can still read back what <see cref="WriteAsync"/> wrote.</summary>
    protected abstract Task<bool> HasTheWriteAsync(TStore store);

    /// <summary>A short stem for the temp file name, so a failure names the store that left the handle open.</summary>
    protected abstract string FileStem { get; }

    [Fact]
    public async Task A_disposed_store_leaves_its_file_closed_and_deletable()
    {
        string path = Path.Combine(Path.GetTempPath(), FileStem + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (TStore store = Open($"Data Source={path}"))
                await WriteAsync(store);

            // Windows: throws IOException while any handle on the file is open.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            // POSIX: the delete above the reopen always succeeds, so the delete alone proves nothing. What a leaked
            // handle cannot survive is the reopen: it is still bound to the unlinked inode, so the pool hands it to
            // the fresh store below and the write comes back out of a database that no longer exists.
            File.Delete(path);
            using TStore reopened = Open($"Data Source={path}");
            Assert.False(await HasTheWriteAsync(reopened));
        }
        finally { File.Delete(path); }
    }
}
