using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>The whole-version action contract against a real SQL Server catalog.</summary>
[Collection(SqlServerCatalogCollection.Name)]
public sealed class SqlServerCatalogVersionRowsTests
{
    [CatalogSqlServerFact]
    public async Task PublishedDiffAndRollback_ReadWholeVersions()
    {
        using var database = new SqlServerCatalogDatabase();
        using var fixture = new CatalogVersionRowsFixture();
        var store = new SqlServerContentAuthoringStore(database.ConnectionString, fixture.Registry, fixture.Pack);
        await CatalogVersionRowsActionTests.AssertScenarioAsync(fixture, store);
    }

    [CatalogSqlServerFact]
    public async Task ReopenedWithPartialRegistry_DiffIgnoresStoredUnregisteredTypes()
    {
        using var database = new SqlServerCatalogDatabase();
        using var fixture = new CatalogVersionRowsFixture();
        var full = new SqlServerContentAuthoringStore(database.ConnectionString, fixture.Registry, fixture.Pack);
        await fixture.SeedAsync(full);
        await CatalogVersionRowsFixture.RetireAsync(full);
        ContentTypeRegistry partial = CatalogFixtures.Registry(CatalogFixtures.ThingSpec);
        var reopened = new SqlServerContentAuthoringStore(database.ConnectionString, partial, fixture.Pack);

        await CatalogVersionRowsPartialRegistryAssertions.ReopenedAsync(reopened, partial);
    }

}
