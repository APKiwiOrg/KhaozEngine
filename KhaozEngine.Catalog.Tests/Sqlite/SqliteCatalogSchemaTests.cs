using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

/// <summary>
/// The schema half of the SQLite provider (spec 4.2): what each mode does to an empty database, and what a
/// mismatched object costs.
/// <para>
/// The property behind all four is one sentence. A production host sets
/// <see cref="ContentAuthoringSchemaMode.ValidateOnly"/> so a connection string pointing at the wrong path
/// cannot silently create a second, empty catalog and serve it, and a refusal is only useful if it names the
/// object and the migration an operator has to apply.
/// </para>
/// </summary>
public class SqliteCatalogSchemaTests
{
    static ContentTypeRegistry Registry() => PublishFixtures.Registry(PublishFixtures.Thing);

    [Fact]
    public async Task AutoCreateOnAnEmptyDatabaseCreatesTheSchemaAndReportsVersionOne()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry());

        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(1, await store.GetSchemaVersionAsync());
        Assert.Equal(0, await store.GetActiveVersionAsync());
        Assert.Null(await store.GetPinnedVersionAsync());

        // The epoch is minted once at creation and is what makes "version 12" answerable across two
        // databases that share no history.
        string epoch = await store.GetStoreEpochAsync();
        Assert.Equal(32, epoch.Length);
    }

    [Fact]
    public async Task ValidateOnlyOnAnEmptyDatabaseRefusesAndNamesTheMigration()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry());

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Contains("catalog-v1-initial", refused.Message, StringComparison.Ordinal);
        Assert.Contains("missing", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidateOnlyOnACorrectSchemaSucceeds()
    {
        using var database = new TemporaryCatalogDatabase();
        using (var created = new SqliteContentAuthoringStore(database.ConnectionString, Registry()))
        {
            await created.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        }

        using var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);

        Assert.Equal(1, await store.GetSchemaVersionAsync());
    }

    [Fact]
    public async Task AMismatchedObjectIsRefusedNamingTheObjectAndTheMigration()
    {
        using var database = new TemporaryCatalogDatabase();
        using (var created = new SqliteContentAuthoringStore(database.ConnectionString, Registry()))
        {
            await created.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        }

        // One index, still present under its own name, over the wrong columns. This is the case a bare
        // "does the table exist" check would pass and a stale deployment really does produce.
        database.Execute(
            """
            DROP INDEX ix_catalog_row_live;
            CREATE INDEX ix_catalog_row_live ON catalog_row(definition_id);
            """);

        using var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry());
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Contains("index:ix_catalog_row_live", refused.Message, StringComparison.Ordinal);
        Assert.Contains("catalog-v1-initial", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnObjectTheSchemaDoesNotDeclareIsRefusedToo()
    {
        using var database = new TemporaryCatalogDatabase();
        using (var created = new SqliteContentAuthoringStore(database.ConnectionString, Registry()))
        {
            await created.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        }

        // The other direction: nothing is missing, something extra is there. A half-applied migration looks
        // exactly like this, and writing to it would write rows the next build cannot read.
        database.Execute("CREATE TABLE catalog_leftover (a INTEGER NOT NULL PRIMARY KEY);");

        using var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry());
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Contains("table:catalog_leftover", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATypeIdPinnedToOneKeyRefusesAnotherKeyUnderIt()
    {
        using var database = new TemporaryCatalogDatabase();
        using (var created = new SqliteContentAuthoringStore(database.ConnectionString, Registry()))
        {
            await created.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        }

        // Same id, different key: a rename. It repoints every row already stored under the old pairing, so
        // it is refused at the door rather than discovered at the first decode.
        ContentTypeRegistry renamed = PublishFixtures.Registry(
            PublishFixtures.Thing with { TypeKey = "renamed_thing" });
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, renamed);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal("unknown-type", refused.Reason);
        Assert.Contains(PublishFixtures.ThingTypeKey, refused.Message, StringComparison.Ordinal);
        Assert.Contains("renamed_thing", refused.Message, StringComparison.Ordinal);
    }
}
