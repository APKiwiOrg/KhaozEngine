using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

[Collection(SqlServerCatalogCollection.Name)]
public sealed class SqlServerSchemaRefusalTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(-2)]
    [InlineData(-3)]
    [InlineData(-999)]
    public void ApplicationLockFailureNamesItsProviderReturnCode(int code)
    {
        var refused = Assert.Throws<ContentAuthoringException>(() =>
            SqlServerCatalogSchemaValidation.RefuseApplicationLockForTests(code));
        Assert.Equal(ContentAuthoringRefusal.SchemaApplicationLockNotGranted, refused.Refusal);
        Assert.Equal(code, refused.ReturnCode);
        Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
        Assert.Equal("schema-mismatch", refused.Reason);
        SqlServerCatalogSchemaValidation.RefuseApplicationLockForTests(0);
        SqlServerCatalogSchemaValidation.RefuseApplicationLockForTests(1);
    }

    [CatalogSqlServerFact]
    public Task MissingSchemaMetadataRefusesWithoutWritingAndOpensAfterRepair()
        => MetadataFaultAsync("DELETE FROM dbo.catalog_metadata;", ContentAuthoringRefusal.SchemaValidationMetadataMissing);

    [CatalogSqlServerFact]
    public Task NullSchemaVersionRefusesWithoutWritingAndOpensAfterRepair()
        => MetadataFaultAsync("ALTER TABLE dbo.catalog_metadata ALTER COLUMN schema_version int NULL; UPDATE dbo.catalog_metadata SET schema_version = NULL;",
            ContentAuthoringRefusal.SchemaValidationMetadataMissing);

    [CatalogSqlServerFact]
    public Task NonintegerSchemaVersionNamesItsClrTypeAndOpensAfterRepair()
        => MetadataFaultAsync("ALTER TABLE dbo.catalog_metadata DROP CONSTRAINT ck_catalog_metadata_version; ALTER TABLE dbo.catalog_metadata ALTER COLUMN schema_version nvarchar(10) NOT NULL; ALTER TABLE dbo.catalog_metadata ADD CONSTRAINT ck_catalog_metadata_version CHECK (schema_version IS NOT NULL); UPDATE dbo.catalog_metadata SET schema_version = N'bad';",
            ContentAuthoringRefusal.SchemaVersionMalformed);

    [CatalogSqlServerFact]
    public Task UnsupportedSchemaVersionNamesTheSupportedVersionAndOpensAfterRepair()
        => MetadataFaultAsync("UPDATE dbo.catalog_metadata SET schema_version = 5;", ContentAuthoringRefusal.SchemaVersionUnsupported);

    static async Task MetadataFaultAsync(string fault, ContentAuthoringRefusal reason)
    {
        using var database = new SqlServerCatalogDatabase();
        var store = await SqlServerCatalogResetHarness.OpenAsync(database);
        database.Execute("SELECT * INTO dbo.host_saved_metadata FROM dbo.catalog_metadata;");
        try
        {
            // Each ALTER completes before the next statement is compiled against the changed column.
            foreach (string statement in fault.Split(';', StringSplitOptions.RemoveEmptyEntries))
                database.Execute(statement);
            string before = SqlServerCatalogNonmutatingOpenTests.Snapshot(database.ConnectionString);
            var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
                store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));
            Assert.Equal(reason, refused.Refusal);
            Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
            Assert.Equal(reason == ContentAuthoringRefusal.SchemaVersionMalformed ? typeof(string) : null, refused.MetadataType);
            if (reason == ContentAuthoringRefusal.SchemaVersionUnsupported)
            {
                Assert.Equal(5L, refused.ActualVersion);
                Assert.Equal(new long[] { 4 }, refused.SupportedVersions);
            }
            Assert.Equal(before, SqlServerCatalogNonmutatingOpenTests.Snapshot(database.ConnectionString));
            database.Execute("DELETE FROM dbo.catalog_metadata; ALTER TABLE dbo.catalog_metadata DROP CONSTRAINT ck_catalog_metadata_version;");
            database.Execute("ALTER TABLE dbo.catalog_metadata ALTER COLUMN schema_version int NOT NULL;");
            database.Execute("INSERT INTO dbo.catalog_metadata SELECT * FROM dbo.host_saved_metadata; ALTER TABLE dbo.catalog_metadata ADD CONSTRAINT ck_catalog_metadata_version CHECK (schema_version >= 1);");
            await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
        }
        finally { database.Execute("DROP TABLE dbo.host_saved_metadata;"); }
    }
}
