using System.Threading.Tasks;
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
}
