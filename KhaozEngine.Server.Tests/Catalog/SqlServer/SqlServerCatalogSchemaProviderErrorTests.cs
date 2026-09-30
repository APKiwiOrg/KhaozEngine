using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// Which provider failures schema validation may call a schema problem. SQL errors 207 and 208 say a named
/// column or object is missing. A lock timeout, deadlock, permission error or cancellation says nothing about
/// the schema, so each remains the provider's own failure rather than advice to apply a migration.
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public class SqlServerCatalogSchemaProviderErrorTests
{
    [Theory]
    [InlineData(207, true)]
    [InlineData(208, true)]
    [InlineData(1205, false)]
    [InlineData(1222, false)]
    [InlineData(-2, false)]
    [InlineData(229, false)]
    public void OnlyAnInvalidColumnOrObjectNameIsASchemaError(int number, bool schema)
        => Assert.Equal(schema, SqlServerCatalogSchemaValidation.IsMissingName(number));

    [CatalogSqlServerFact]
    public async Task ACatalogWithoutItsMetadataTableIsStillRefusedAsUnreadable()
    {
        using var database = new SqlServerCatalogDatabase();
        database.Execute("CREATE TABLE dbo.catalog_orphan (id int NOT NULL PRIMARY KEY);");
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            SqlServerCatalogSchemaValidation.InitializeAsync(
                connection, ContentAuthoringSchemaMode.ValidateOnly, CancellationToken.None));

        Assert.Equal(ContentAuthoringException.SchemaMismatchReason, refused.Reason);
        Assert.Contains("unreadable", refused.Message, StringComparison.Ordinal);
    }

    [CatalogSqlServerFact]
    public async Task ALockedMetadataTableReportsTheLockRatherThanASchemaToMigrate()
    {
        using var database = new SqlServerCatalogDatabase();
        var created = new SqlServerContentAuthoringStore(database.ConnectionString, CatalogFixtures.Registry(
            CatalogFixtures.ThingSpec));
        await created.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        await using var holder = new SqlConnection(database.ConnectionString);
        await holder.OpenAsync();
        await using SqlTransaction held = (SqlTransaction)await holder.BeginTransactionAsync();
        await ScalarAsync(holder, held, "SELECT COUNT(*) FROM dbo.catalog_metadata WITH (TABLOCKX, HOLDLOCK);");

        await using var validating = new SqlConnection(database.ConnectionString);
        await validating.OpenAsync();
        await SetLockTimeoutAsync(validating);

        SqlException locked = await Assert.ThrowsAsync<SqlException>(() =>
            SqlServerCatalogSchemaValidation.InitializeAsync(
                validating, ContentAuthoringSchemaMode.ValidateOnly, CancellationToken.None));

        Assert.Equal(1222, locked.Number);
        await held.RollbackAsync();
    }

    [CatalogSqlServerFact]
    public async Task CancellationRemainsCancellation()
    {
        using var database = new SqlServerCatalogDatabase();
        await using var connection = new SqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlServerCatalogSchemaValidation.InitializeAsync(
                connection, ContentAuthoringSchemaMode.ValidateOnly, cancelled.Token));
    }

    static async Task<int> ScalarAsync(SqlConnection connection, SqlTransaction transaction, string sql)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return await command.ExecuteScalarAsync() is int value ? value : -1;
    }

    static async Task SetLockTimeoutAsync(SqlConnection connection)
    {
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = "SET LOCK_TIMEOUT 100;";
        await command.ExecuteNonQueryAsync();
    }
}
