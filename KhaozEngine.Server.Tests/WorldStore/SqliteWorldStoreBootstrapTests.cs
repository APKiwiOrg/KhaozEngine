using System.Threading.Tasks;
using KhaozEngine.Tests.Sqlite;
using KhaozEngine.WorldStore.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore;

public sealed class SqliteWorldStoreBootstrapTests
{
    [Fact]
    public async Task Current_schema_opens_while_another_connection_holds_a_write_transaction()
    {
        using var file = new SqliteScratchFile("ke-world-bootstrap-");
        using (var initial = new SqliteWorldStore(file.ConnectionString))
            await initial.SaveAsync("saved", new byte[] { 1, 2 });

        using var writer = new SqliteConnection(file.ConnectionString + ";Pooling=False");
        writer.Open();
        using SqliteTransaction transaction = writer.BeginTransaction(deferred: false);

        using var reopened = new SqliteWorldStore(file.ConnectionString + ";Default Timeout=1");
        Assert.Equal(new byte[] { 1, 2 }, await reopened.LoadAsync("saved"));
    }

    [Fact]
    public async Task Current_schema_opens_on_a_read_only_connection()
    {
        using var file = new SqliteScratchFile("ke-world-read-bootstrap-");
        using (var initial = new SqliteWorldStore(file.ConnectionString))
            await initial.SaveAsync("saved", new byte[] { 3 });

        using var reopened = new SqliteWorldStore(file.ConnectionString + ";Mode=ReadOnly");
        Assert.Equal(new byte[] { 3 }, await reopened.LoadAsync("saved"));
    }
}
