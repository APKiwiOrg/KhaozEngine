using System.Data;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The reset behind the schema's application lock, which is the create's own lock on the create's own
/// resource name.
/// <para>
/// <b>There is no race to lose here.</b> While a second session holds the lock exclusively the reset cannot
/// be past its first statement, so asserting that it has not finished is a fact rather than a timing guess,
/// and the catalog it would have replaced is still whole. Releasing the lock lets it through.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public class SqlServerCatalogResetLockTests
{
    /// <summary>How long the holder keeps the lock before letting the reset through.</summary>
    const int HeldMilliseconds = 1500;

    [CatalogSqlServerFact]
    public async Task AResetWaitsBehindTheLockTheSchemaCreateTakes()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await SqlServerCatalogResetHarness.OpenAsync(database);
        await SqlServerCatalogResetHarness.Seed(store, "one", "two");

        await using var holder = new SqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        await using SqlTransaction held = (SqlTransaction)await holder.BeginTransactionAsync();
        Assert.True(await TakeAsync(holder, held) >= 0, "The holder must get the lock before the reset runs.");

        Task<ContentCatalogResetResult> reset = SqlServerCatalogReset.ResetAsync(
            database.ConnectionString,
            SqlServerCatalogResetHarness.Actor,
            SqlServerCatalogResetHarness.Operator,
            "content release");

        await Task.Delay(HeldMilliseconds);

        // Not merely unfinished. The lock is taken before the reset READS anything, so the catalog it would
        // have replaced is untouched too.
        Assert.False(reset.IsCompleted);
        Assert.Equal(1, database.Scalar("SELECT active_version FROM dbo.catalog_metadata;"));
        Assert.Equal(2, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_row;"));

        await held.RollbackAsync();

        ContentCatalogResetResult done = await reset;
        Assert.Equal(1, done.ActiveVersion);
        Assert.Equal(0, database.Scalar("SELECT active_version FROM dbo.catalog_metadata;"));
    }

    /// <summary>
    /// The same lock, on the same resource name the provider names, taken with no wait at all so a failure to
    /// get it is reported here rather than hanging the test.
    /// </summary>
    static async Task<int> TakeAsync(SqlConnection connection, SqlTransaction transaction)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock
                @Resource = @resource,
                @LockMode = 'Exclusive',
                @LockOwner = 'Transaction',
                @LockTimeout = 0;
            SELECT @result;
            """;
        command.Parameters.Add("@resource", SqlDbType.NVarChar, 255).Value =
            SqlServerCatalogSchemaValidation.LockResource;
        return await command.ExecuteScalarAsync() is int code ? code : -999;
    }
}
