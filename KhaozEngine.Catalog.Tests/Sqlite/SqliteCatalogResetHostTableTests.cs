using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The tables in the file that are NOT the catalog's, which a SQLite host is entitled to keep beside it.
/// <para>
/// <b>A name pattern cannot tell them apart, which is why neither the drop nor the count may be one.</b> In
/// SQLite's <c>LIKE</c> an underscore matches ANY single character, so <c>'catalog_%'</c> matches
/// <c>catalogs</c> and <c>cataloguer</c> as well, and a reset driven by that pattern drops a host's data.
/// Escaping the underscore fixes those two and still cannot save <c>catalog_overrides_by_host</c>, which
/// really does start with <c>catalog_</c>: only the schema's own inventory can, and that is the rule the drop
/// runs under.
/// </para>
/// </summary>
public class SqliteCatalogResetHostTableTests
{
    [Fact]
    public async Task AResetLeavesTheHostsOwnTablesAndEveryRowInThemStanding()
    {
        using var database = new TemporaryCatalogDatabase();
        using (var store = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await SqliteCatalogResetHarness.Seed(store, "one", "two");
        }

        SeedHostTables(database);

        await SqliteCatalogReset.ResetAsync(
            database.ConnectionString,
            SqliteCatalogResetHarness.Actor,
            SqliteCatalogResetHarness.Operator,
            "content release");

        // Every row of every host table, counted and read back. Not merely the table still standing: a drop
        // and a recreate by the host's own DDL would leave the table there and the rows gone.
        Assert.Equal(2L, database.Scalar("SELECT COUNT(*) FROM catalogs;"));
        Assert.Equal("host", SqliteCatalogResetHarness.Text(database, "SELECT label FROM catalogs WHERE id = 1;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM cataloguer;"));
        Assert.Equal(7L, database.Scalar("SELECT id FROM cataloguer;"));
        Assert.Equal(1L, database.Scalar("SELECT COUNT(*) FROM catalog_overrides_by_host;"));
        Assert.Equal(9L, database.Scalar("SELECT id FROM catalog_overrides_by_host;"));

        // And the catalog itself really was replaced, so the assertions above are not passing on a reset that
        // did nothing at all.
        Assert.Equal(0L, database.Scalar("SELECT active_version FROM catalog_metadata;"));
        Assert.Equal(0L, database.Scalar("SELECT COUNT(*) FROM catalog_row;"));
    }

    [Fact]
    public async Task AHostTableWhoseNameTheWildcardMatchesDoesNotStopTheInitializerOpening()
    {
        using var database = new TemporaryCatalogDatabase();

        // Created BEFORE the catalog schema exists, which is the order a host that owns the file works in.
        // Under an unescaped 'catalog_%' the initializer counts these as catalog objects, decides the schema
        // is already there, and then refuses because none of the tables it validates exists.
        database.Execute("CREATE TABLE catalogs(id INTEGER NOT NULL PRIMARY KEY, label TEXT NOT NULL);");
        database.Execute("CREATE TABLE cataloguer(id INTEGER NOT NULL PRIMARY KEY);");

        using var store = new SqliteContentAuthoringStore(
            database.ConnectionString, SqliteCatalogResetHarness.Registry(), database.Pack());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(SqliteCatalogSchema.CurrentVersion, await store.GetSchemaVersionAsync());
        Assert.Equal(0, await store.GetActiveVersionAsync());
    }

    [Fact]
    public void TheInventoryHoldsTheSchemasOwnTablesAndNoHostTableTheWildcardWouldSweepIn()
    {
        Assert.Contains("catalog_metadata", SqliteCatalogSchemaInventory.Tables, StringComparer.Ordinal);
        Assert.Contains("catalog_audit", SqliteCatalogSchemaInventory.Tables, StringComparer.Ordinal);
        Assert.DoesNotContain("catalogs", SqliteCatalogSchemaInventory.Tables, StringComparer.Ordinal);
        Assert.DoesNotContain("cataloguer", SqliteCatalogSchemaInventory.Tables, StringComparer.Ordinal);
        Assert.DoesNotContain(
            "catalog_overrides_by_host", SqliteCatalogSchemaInventory.Tables, StringComparer.Ordinal);
        Assert.DoesNotContain("sqlite_sequence", SqliteCatalogSchemaInventory.Tables, StringComparer.Ordinal);
    }

    /// <summary>
    /// Three host tables with rows in them: two the wildcard matches by accident, and one that starts with
    /// <c>catalog_</c> for real and is therefore indistinguishable from an engine table under ANY name rule.
    /// </summary>
    static void SeedHostTables(TemporaryCatalogDatabase database)
    {
        database.Execute("CREATE TABLE catalogs(id INTEGER NOT NULL PRIMARY KEY, label TEXT NOT NULL);");
        database.Execute("INSERT INTO catalogs(id, label) VALUES (1, 'host'), (2, 'rows');");
        database.Execute("CREATE TABLE cataloguer(id INTEGER NOT NULL PRIMARY KEY);");
        database.Execute("INSERT INTO cataloguer(id) VALUES (7);");
        database.Execute("CREATE TABLE catalog_overrides_by_host(id INTEGER NOT NULL PRIMARY KEY);");
        database.Execute("INSERT INTO catalog_overrides_by_host(id) VALUES (9);");
    }
}
