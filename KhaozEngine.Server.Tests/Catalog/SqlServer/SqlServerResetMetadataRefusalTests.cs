using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

[Collection(SqlServerCatalogCollection.Name)]
public sealed class SqlServerResetMetadataRefusalTests
{
    [CatalogSqlServerFact]
    public Task MissingActiveVersionRefusesResetWithoutChangingContent()
        => RefuseWholeAsync("ALTER TABLE dbo.catalog_metadata DROP COLUMN active_version;",
            ContentAuthoringRefusal.SchemaMetadataUnreadable);

    [CatalogSqlServerFact]
    public Task NullActiveVersionRefusesResetWithoutChangingContent()
        => RefuseWholeAsync("ALTER TABLE dbo.catalog_metadata ALTER COLUMN active_version int NULL; UPDATE dbo.catalog_metadata SET active_version = NULL;",
            ContentAuthoringRefusal.SchemaValidationMetadataMissing);

    [CatalogSqlServerFact]
    public Task WrongClrTypeActiveVersionRefusesResetWithoutChangingContent()
        => RefuseWholeAsync("ALTER TABLE dbo.catalog_metadata ALTER COLUMN active_version bigint NOT NULL;",
            ContentAuthoringRefusal.StoredScalarTypeMismatch);

    static async Task RefuseWholeAsync(string fault, ContentAuthoringRefusal reason)
    {
        using var database = new SqlServerCatalogDatabase();
        await SqlServerCatalogResetHarness.OpenAsync(database);
        database.Execute("ALTER TABLE dbo.catalog_metadata DROP CONSTRAINT ck_catalog_metadata_active; ALTER TABLE dbo.catalog_metadata DROP CONSTRAINT df_catalog_metadata_active; " + fault);
        string before = SqlServerCatalogNonmutatingOpenTests.Snapshot(database.ConnectionString);
        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            SqlServerCatalogReset.ResetAsync(database.ConnectionString, "actor", "", ""));
        Assert.Equal(reason, refused.Refusal);
        Assert.Equal("schema-mismatch", refused.Reason);
        if (reason == ContentAuthoringRefusal.StoredScalarTypeMismatch)
        {
            Assert.Equal(typeof(int), refused.ExpectedScalarType);
            Assert.Equal(typeof(long), refused.ActualScalarType);
        }
        else
        {
            Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
            if (reason == ContentAuthoringRefusal.SchemaMetadataUnreadable)
                Assert.Equal(207, Assert.IsType<SqlException>(refused.InnerException).Number);
        }
        Assert.Equal(before, SqlServerCatalogNonmutatingOpenTests.Snapshot(database.ConnectionString));
        if (reason == ContentAuthoringRefusal.SchemaMetadataUnreadable)
            database.Execute("ALTER TABLE dbo.catalog_metadata ADD active_version int NULL;");
        database.Execute("UPDATE dbo.catalog_metadata SET active_version = 0; ALTER TABLE dbo.catalog_metadata ALTER COLUMN active_version int NOT NULL;");
        Assert.Equal(4, (await SqlServerCatalogReset.ResetAsync(database.ConnectionString, "actor", "", "")).SchemaVersion);
    }

    [CatalogSqlServerFact]
    public Task AHostSeedTriggerRemovingMetadataRefusesResetAndRollsBackItsWrite()
        => RefuseRecreationAsync("", "DELETE FROM dbo.catalog_metadata;", ContentAuthoringRefusal.SchemaValidationMetadataMissing, null, null);

    [CatalogSqlServerFact]
    public Task AHostSeedTriggerNullingTheEpochRefusesResetAndRollsBackItsWrite()
        => RefuseRecreationAsync("ALTER TABLE dbo.catalog_metadata ALTER COLUMN store_epoch nvarchar(36) NULL;",
            "UPDATE dbo.catalog_metadata SET store_epoch = NULL;", ContentAuthoringRefusal.SchemaValidationMetadataMissing, null, null);

    [CatalogSqlServerFact]
    public Task AHostSeedTriggerChangingTheEpochTypeRefusesResetAndRollsBackItsWrite()
        => RefuseRecreationAsync("ALTER TABLE dbo.catalog_metadata ALTER COLUMN store_epoch sql_variant NULL;",
            "UPDATE dbo.catalog_metadata SET store_epoch = 0x01;", ContentAuthoringRefusal.StoredScalarTypeMismatch, typeof(string), typeof(byte[]));

    [CatalogSqlServerFact]
    public Task AHostSeedTriggerChangingTheVersionTypeRefusesResetAndRollsBackItsWrite()
        => RefuseRecreationAsync("ALTER TABLE dbo.catalog_metadata ALTER COLUMN schema_version sql_variant NULL;",
            "UPDATE dbo.catalog_metadata SET schema_version = CONVERT(bigint, 4);", ContentAuthoringRefusal.StoredScalarTypeMismatch, typeof(int), typeof(long));

    static async Task RefuseRecreationAsync(string reshape, string seedFault, ContentAuthoringRefusal reason, Type? expectedType, Type? actualType)
    {
        using var database = new SqlServerCatalogDatabase();
        var store = await SqlServerCatalogResetHarness.OpenAsync(database);
        await SqlServerCatalogResetHarness.Seed(store, "one", "two");
        database.Execute("CREATE TABLE dbo.host_recreate_probe(value int NOT NULL);");
        // A database DDL trigger survives the table drops and installs a seed trigger on the new metadata.
        // Its side write would persist without rollback, even when it discards the seed itself.
        string install = "CREATE TRIGGER dbo.host_metadata_seed ON dbo.catalog_metadata AFTER INSERT AS BEGIN SET NOCOUNT ON; INSERT INTO dbo.host_recreate_probe VALUES (1); " + seedFault + " END;";
        try
        {
            database.Execute($"""
            CREATE TRIGGER host_recreate_metadata ON DATABASE FOR CREATE_TABLE AS
            BEGIN
                SET NOCOUNT ON;
                IF EVENTDATA().value('(/EVENT_INSTANCE/SchemaName)[1]', 'nvarchar(128)') = N'dbo'
                   AND EVENTDATA().value('(/EVENT_INSTANCE/ObjectName)[1]', 'nvarchar(128)') = N'catalog_metadata'
                BEGIN
                    ALTER TABLE dbo.catalog_metadata DROP CONSTRAINT ck_catalog_metadata_epoch;
                    ALTER TABLE dbo.catalog_metadata DROP CONSTRAINT ck_catalog_metadata_version;
                    {reshape}
                    EXEC(N'{install.Replace("'", "''", StringComparison.Ordinal)}');
                END;
            END;
            """);
            string before = SqlServerCatalogNonmutatingOpenTests.Snapshot(database.ConnectionString);
            var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
                SqlServerCatalogReset.ResetAsync(database.ConnectionString, "actor", "", ""));
            Assert.Equal(reason, refused.Refusal);
            Assert.Equal("schema-mismatch", refused.Reason);
            if (expectedType is null) Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
            else
            {
                Assert.Equal(expectedType, refused.ExpectedScalarType);
                Assert.Equal(actualType, refused.ActualScalarType);
            }
            Assert.Equal(before, SqlServerCatalogNonmutatingOpenTests.Snapshot(database.ConnectionString));
            Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.host_recreate_probe;"));
            database.Execute("DROP TRIGGER host_recreate_metadata ON DATABASE;");
            var reset = await SqlServerCatalogReset.ResetAsync(database.ConnectionString, "actor", "", "");
            Assert.Equal(4, reset.SchemaVersion);
            Assert.Equal(32, reset.StoreEpoch.Length);
        }
        finally
        {
            database.Execute("DROP TRIGGER IF EXISTS host_recreate_metadata ON DATABASE; DROP TABLE dbo.host_recreate_probe;");
        }
    }
}
