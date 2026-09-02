using System.Threading.Tasks;
using KhaozEngine.Tests.Sqlite;
using KhaozEngine.WorldStore.Sqlite;

namespace KhaozEngine.Tests.WorldStore;

/// <summary>
/// The world store's half of the shared release contract (see <see cref="SqliteStoreFileLifetimeContract{TStore}"/>
/// for what it proves and why both of its assertions are needed). This is #713, the first of the three copies of
/// the same leak, and the store now inherits the fix from <c>SqliteStoreConnection</c> rather than carrying it.
/// </summary>
public sealed class SqliteWorldStoreFileLifetimeTests : SqliteStoreFileLifetimeContract<SqliteWorldStore>
{
    protected override string FileStem => "ke-ws-life-";

    protected override SqliteWorldStore Open(string connectionString) => new(connectionString);

    protected override Task WriteAsync(SqliteWorldStore store) => store.SaveAsync("k", new byte[] { 1, 2 });

    protected override async Task<bool> HasTheWriteAsync(SqliteWorldStore store)
        => await store.LoadAsync("k") is not null;
}
