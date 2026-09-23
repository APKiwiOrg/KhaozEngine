using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The tables in the database that are NOT the catalog's, mirroring <c>SqliteCatalogResetHostTableTests</c>.
/// <para>
/// This backend's <c>catalog[_]%</c> pattern escapes its underscore already, so <c>catalogs</c> and
/// <c>cataloguer</c> were never at risk here. <c>catalog_overrides_by_host</c> always was, under any name
/// rule at all, and the fix is the same on both providers: the drop names the schema's own INVENTORY.
/// </para>
/// <para>
/// The first fact pins what the RESET does with such a table. It does not reopen the store over it, and the
/// store's validator refuses a table inside the <c>catalog_</c> namespace that the schema does not declare.
/// The rest pin a host table with a foreign key INTO the catalog, one fact per delete action.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public class SqlServerCatalogResetHostTableTests
{
    [CatalogSqlServerFact]
    public async Task AResetLeavesTheHostsOwnTablesAndEveryRowInThemStanding()
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await SqlServerCatalogResetHarness.OpenAsync(database);
        await SqlServerCatalogResetHarness.Seed(store, "one", "two");

        DropHostTables(database);
        database.Execute("CREATE TABLE dbo.catalogs(id int NOT NULL PRIMARY KEY, label nvarchar(32) NOT NULL);");
        database.Execute("INSERT INTO dbo.catalogs(id, label) VALUES (1, N'host'), (2, N'rows');");
        database.Execute("CREATE TABLE dbo.cataloguer(id int NOT NULL PRIMARY KEY);");
        database.Execute("INSERT INTO dbo.cataloguer(id) VALUES (7);");
        database.Execute("CREATE TABLE dbo.catalog_overrides_by_host(id int NOT NULL PRIMARY KEY);");
        database.Execute("INSERT INTO dbo.catalog_overrides_by_host(id) VALUES (9);");

        try
        {
            await SqlServerCatalogReset.ResetAsync(
                database.ConnectionString,
                SqlServerCatalogResetHarness.Actor,
                SqlServerCatalogResetHarness.Operator,
                "content release");

            // Every row of every host table, counted and read back. Not merely the table still standing.
            Assert.Equal(2, database.Scalar("SELECT COUNT(*) FROM dbo.catalogs;"));
            Assert.Equal(
                "host",
                SqlServerCatalogResetHarness.Text(database, "SELECT label FROM dbo.catalogs WHERE id = 1;"));
            Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.cataloguer;"));
            Assert.Equal(7, database.Scalar("SELECT id FROM dbo.cataloguer;"));
            Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_overrides_by_host;"));
            Assert.Equal(9, database.Scalar("SELECT id FROM dbo.catalog_overrides_by_host;"));

            // And the catalog itself really was replaced, so the assertions above are not passing on a reset
            // that did nothing at all.
            Assert.Equal(0, database.Scalar("SELECT active_version FROM dbo.catalog_metadata;"));
            Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_row;"));
        }
        finally
        {
            // The fixture's own sweep is by name pattern and would leave catalogs and cataloguer standing for
            // every class after this one.
            DropHostTables(database);
        }
    }

    // SQLite's DROP TABLE deletes every row first and fires a host key's delete action, which is why that
    // reset refuses such a key up front. SQL Server has no such delete: it refuses to drop a table any
    // foreign key references, whatever the key's action, and the reset rolls back with the host untouched.
    [CatalogSqlServerFact]
    public Task AHostKeyDeclaredCascadeFailsTheResetAndTheHostRowsStandUnchanged()
        => AssertHostKeyFailsTheResetAsync("CASCADE");

    [CatalogSqlServerFact]
    public Task AHostKeyDeclaredSetNullFailsTheResetAndTheHostRowsStandUnchanged()
        => AssertHostKeyFailsTheResetAsync("SET NULL");

    [CatalogSqlServerFact]
    public Task AHostKeyDeclaredSetDefaultFailsTheResetAndTheHostRowsStandUnchanged()
        => AssertHostKeyFailsTheResetAsync("SET DEFAULT");

    static async Task AssertHostKeyFailsTheResetAsync(string action)
    {
        using var database = new SqlServerCatalogDatabase();
        SqlServerContentAuthoringStore store = await SqlServerCatalogResetHarness.OpenAsync(database);
        await SqlServerCatalogResetHarness.Seed(store, "one", "two");
        string epochBefore = SqlServerCatalogResetHarness.Text(database, "SELECT store_epoch FROM dbo.catalog_metadata;");

        database.Execute(
            $"""
            CREATE TABLE dbo.host_notes(
                id int NOT NULL PRIMARY KEY,
                version_number int NULL CONSTRAINT fk_host_notes_version
                    REFERENCES dbo.catalog_version(version_number) ON DELETE {action},
                body nvarchar(32) NOT NULL);
            INSERT INTO dbo.host_notes(id, version_number, body) VALUES (1, 1, N'first'), (2, 1, N'second');
            """);

        try
        {
            SqlException failed = await Assert.ThrowsAsync<SqlException>(
                () => SqlServerCatalogReset.ResetAsync(
                    database.ConnectionString,
                    SqlServerCatalogResetHarness.Actor,
                    SqlServerCatalogResetHarness.Operator,
                    "content release"));

            Assert.Equal(3726, failed.Number);
            Assert.Equal("1,1,first;2,1,second", SqlServerCatalogResetHarness.Text(database, HostRowsSql));
            Assert.Equal(epochBefore, SqlServerCatalogResetHarness.Text(database, "SELECT store_epoch FROM dbo.catalog_metadata;"));
            Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_version;"));
            Assert.Equal(2, database.Scalar("SELECT COUNT(*) FROM dbo.catalog_row;"));

            var reopened = new SqlServerContentAuthoringStore(
                database.ConnectionString, SqlServerCatalogResetHarness.Registry(), database.Pack());
            await reopened.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
            Assert.Equal(1, await reopened.GetActiveVersionAsync());
        }
        finally
        {
            // The fixture's sweep cannot drop catalog_version while this stands.
            database.Execute("DROP TABLE IF EXISTS dbo.host_notes;");
        }
    }

    /// <summary>Every host row in id order, a NULL written as the word, so a rewritten value reads differently.</summary>
    const string HostRowsSql = """
        SELECT STRING_AGG(CONCAT(id, N',', COALESCE(CAST(version_number AS nvarchar(11)), N'NULL'), N',', body), N';')
            WITHIN GROUP (ORDER BY id)
        FROM dbo.host_notes;
        """;

    static void DropHostTables(SqlServerCatalogDatabase database) => database.Execute(
        """
        DROP TABLE IF EXISTS dbo.catalogs;
        DROP TABLE IF EXISTS dbo.cataloguer;
        DROP TABLE IF EXISTS dbo.catalog_overrides_by_host;
        """);
}
