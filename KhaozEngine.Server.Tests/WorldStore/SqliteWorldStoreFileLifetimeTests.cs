using System;
using System.IO;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

/// <summary>
/// A disposed store holds no OS handle on its database file. Microsoft.Data.Sqlite pools connections by default,
/// so <c>SqliteConnection.Dispose()</c> hands the native handle back to the pool instead of closing it, and the
/// file stays open for as long as the pool holds it. That leak reads differently per platform and is a defect on
/// all of them: Windows refuses to delete or exclusively open a file something still has open (#713, which took
/// both Windows GPU legs down on an unrelated branch), while POSIX unlinks it happily and then hands the SAME
/// leaked handle to the next store opened on that path, which quietly serves the deleted database. A server
/// rotating or dropping a store file hits one or the other depending on where it runs.
///
/// Both assertions below earn their place, because neither one covers every platform on its own: the exclusive
/// open is what bites on Windows and is vacuous on POSIX (.NET implements FileShare with flock, SQLite locks with
/// fcntl, and the two never collide), and the delete-then-reopen is what bites on POSIX.
/// </summary>
public sealed class SqliteWorldStoreFileLifetimeTests
{
    private static string TempDb()
        => Path.Combine(Path.GetTempPath(), "ke-ws-life-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public async Task A_disposed_store_leaves_its_file_closed_and_deletable()
    {
        string path = TempDb();
        try
        {
            using (var store = new SqliteWorldStore($"Data Source={path}"))
                await store.SaveAsync("k", new byte[] { 1, 2 });

            // Windows: throws IOException while any handle on the file is open.
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            // POSIX: the delete above the reopen always succeeds, so the delete alone proves nothing. What a
            // leaked handle cannot survive is the reopen: it is still bound to the unlinked inode, so the pool
            // hands it to the fresh store below and "k" comes back out of a database that no longer exists.
            File.Delete(path);
            using var reopened = new SqliteWorldStore($"Data Source={path}");
            Assert.Null(await reopened.LoadAsync("k"));
        }
        finally { File.Delete(path); }
    }
}
