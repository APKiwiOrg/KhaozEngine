using System;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Authoring;
using KhaozEngine.Tests.Gpu;
using Xunit;
using static KhaozEngine.Tests.Catalog.Authoring.TextAuthoringFixtures;

namespace KhaozEngine.Tests.Catalog.Sqlite;

public sealed class SqliteStoreRefusalTests : StoreRefusalTests
{
    protected override bool UsesSqlite => true;

    [Fact]
    public void SqliteStoreNeverConstructsAMessageOnlyRefusal()
    {
        var legacy = typeof(ContentAuthoringException).GetConstructors()
            .Where(constructor => constructor.GetParameters().All(parameter => parameter.ParameterType != typeof(ContentAuthoringRefusal)));
        Assert.DoesNotContain(RefusalConstructorFence.EngineCallers([typeof(SqliteContentAuthoringStore).Assembly], legacy),
            caller => caller.StartsWith("KhaozEngine.Catalog.Sqlite.SqliteContentAuthoringStore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingMetadataRefusesReadsUntilTheCatalogRowIsRestored()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, TextRegistry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        database.Execute("CREATE TABLE saved_metadata AS SELECT * FROM catalog_metadata; DELETE FROM catalog_metadata;");
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.GetActiveVersionAsync());
        Assert.Equal(ContentAuthoringRefusal.SchemaMetadataMissing, refused.Refusal);
        Assert.Equal("schema-mismatch", refused.Reason);
        database.Execute("INSERT INTO catalog_metadata SELECT * FROM saved_metadata; DROP TABLE saved_metadata;");
        Assert.Equal(0, await store.GetActiveVersionAsync());
    }
}
