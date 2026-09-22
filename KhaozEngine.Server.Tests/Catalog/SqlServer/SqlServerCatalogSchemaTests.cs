using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.Catalog.SqlServer;

/// <summary>
/// The schema half of the SQL Server provider (spec 4.2, 4.5): what each mode does to a database carrying no
/// catalog table, and what a mismatched object costs.
/// <para>
/// The property behind all four is one sentence. A production host sets
/// <see cref="ContentAuthoringSchemaMode.ValidateOnly"/> so a connection string pointing at the wrong database
/// cannot silently create a second, empty catalog and serve it, and a refusal is only useful if it names the
/// object and the migration an operator has to apply.
/// </para>
/// <para>
/// The mismatch case is a renamed CONSTRAINT rather than a changed one, which is the case this provider is
/// shaped around: SQL Server generates a name for an unnamed constraint, so the DDL names every one and
/// validation compares names. A constraint under the wrong name is a constraint validation cannot vouch for.
/// </para>
/// </summary>
[Collection(SqlServerCatalogCollection.Name)]
public class SqlServerCatalogSchemaTests
{
    static ContentTypeRegistry Registry() => CatalogFixtures.Registry(CatalogFixtures.ThingSpec);

    [CatalogSqlServerFact]
    public async Task AutoCreateOnAnEmptyDatabaseCreatesTheSchemaAndReportsVersionTwo()
    {
        using var database = new SqlServerCatalogDatabase();
        var store = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());

        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(2, await store.GetSchemaVersionAsync());
        Assert.Equal(0, await store.GetActiveVersionAsync());
        Assert.Null(await store.GetPinnedVersionAsync());

        // The epoch is minted once at creation and is what makes "version 12" answerable across two databases
        // that share no history.
        string epoch = await store.GetStoreEpochAsync();
        Assert.Equal(32, epoch.Length);

        // AutoCreate validates what it created, so this run also pins the expectation lists against the DDL
        // file they were transcribed from. Fifteen tables: the fourteen of spec 4.3 and the content upgrade
        // ledger version 2 adds.
        Assert.Equal(15, database.Scalar(
            """
            SELECT COUNT(*) FROM sys.tables
            WHERE schema_id = SCHEMA_ID(N'dbo') AND name LIKE N'catalog[_]%';
            """));
    }

    [CatalogSqlServerFact]
    public async Task ValidateOnlyOnAnEmptyDatabaseRefusesAndNamesTheMigration()
    {
        using var database = new SqlServerCatalogDatabase();
        var store = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Contains("catalog-v2-content-upgrade-ledger", refused.Message, StringComparison.Ordinal);
        Assert.Contains("missing", refused.Message, StringComparison.Ordinal);
    }

    [CatalogSqlServerFact]
    public async Task ValidateOnlyOnACorrectSchemaSucceeds()
    {
        using var database = new SqlServerCatalogDatabase();
        var created = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        await created.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        var store = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        Assert.Equal(2, await store.GetSchemaVersionAsync());
    }

    [CatalogSqlServerFact]
    public async Task AConstraintUnderTheWrongNameIsRefusedNamingItAndTheMigration()
    {
        using var database = new SqlServerCatalogDatabase();
        var created = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        await created.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        // The same rule, still enforced, under a name version 1 does not declare. A half-applied migration
        // that re-added a constraint without its name produces exactly this, and a bare "does the table exist"
        // check would pass it.
        database.Execute(
            """
            ALTER TABLE dbo.catalog_row DROP CONSTRAINT ck_catalog_row_retired;
            ALTER TABLE dbo.catalog_row ADD CONSTRAINT ck_catalog_row_retired_v2 CHECK (retired IN (0, 1));
            """);

        var store = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Contains("catalog_row.ck_catalog_row_retired", refused.Message, StringComparison.Ordinal);
        Assert.Contains("catalog-v2-content-upgrade-ledger", refused.Message, StringComparison.Ordinal);
    }

    [CatalogSqlServerFact]
    public async Task ATypeIdPinnedToOneKeyRefusesAnotherKeyUnderIt()
    {
        using var database = new SqlServerCatalogDatabase();
        var created = new SqlServerContentAuthoringStore(database.ConnectionString, Registry());
        await created.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        // Same id, different key: a rename. It repoints every row already stored under the old pairing, so it
        // is refused at the door rather than discovered at the first decode.
        ContentTypeRegistry renamed = CatalogFixtures.Registry(
            CatalogFixtures.ThingSpec with { TypeKey = "renamed_thing" });
        var store = new SqlServerContentAuthoringStore(database.ConnectionString, renamed);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal("unknown-type", refused.Reason);
        Assert.Contains(CatalogFixtures.ThingTypeKey, refused.Message, StringComparison.Ordinal);
        Assert.Contains("renamed_thing", refused.Message, StringComparison.Ordinal);
    }
}
