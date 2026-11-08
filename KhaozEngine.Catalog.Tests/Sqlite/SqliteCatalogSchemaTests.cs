using System;
using System.Threading.Tasks;
using KhaozEngine.Catalog;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using KhaozEngine.Tests.Catalog.Publish;
using Microsoft.Data.Sqlite;
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
    public async Task AutoCreateOnAnEmptyDatabaseCreatesTheSchemaAndReportsVersionFour()
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry());

        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);

        Assert.Equal(4, await store.GetSchemaVersionAsync());
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
        Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
        Assert.Equal(ContentAuthoringRefusal.SchemaMissing, refused.Refusal);
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
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

        Assert.Equal(4, await store.GetSchemaVersionAsync());
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
        Assert.Equal(ContentAuthoringRefusal.SchemaObjectDefinitionMismatch, refused.Refusal);
        Assert.Equal("index:ix_catalog_row_live", refused.ObjectName);
        Assert.Equal(4, refused.ExpectedVersion);
        Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
        database.Execute("DROP INDEX ix_catalog_row_live; CREATE INDEX ix_catalog_row_live ON catalog_row(type_id, replaced_in_version, definition_id);");
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
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
        Assert.Equal(ContentAuthoringRefusal.SchemaObjectUnexpected, refused.Refusal);
        Assert.Equal("table:catalog_leftover", refused.ObjectName);
        Assert.Equal(4, refused.ExpectedVersion);
        Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
        database.Execute("DROP TABLE catalog_leftover;");
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
    }

    [Theory]
    [InlineData(1024, "renamed_thing", 1024L)]
    [InlineData(1025, "thing", 1024L)]
    [InlineData(1024, "thing", 66560L)]
    public async Task StoredTypeIdentityRefusesARenameOrReassignment(ushort requestedTypeId, string requestedKey, long storedTypeId)
    {
        using var database = new TemporaryCatalogDatabase();
        using (var created = new SqliteContentAuthoringStore(database.ConnectionString, Registry()))
        {
            await created.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        }

        if (storedTypeId != 1024)
        {
            // A host can store an integer wider than the public type id without changing the DDL.
            database.Execute("PRAGMA ignore_check_constraints = ON; UPDATE catalog_type SET type_id = 66560 WHERE type_id = 1024;");
        }

        // A rename or a reassignment repoints rows under the stored pairing, so initialization refuses it
        // before any registration can be synchronized.
        ContentTypeRegistry renamed = PublishFixtures.Registry(
            PublishFixtures.Thing with { TypeId = requestedTypeId, TypeKey = requestedKey });
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, renamed);

        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal("unknown-type", refused.Reason);
        Assert.Equal(ContentAuthoringRefusal.TypeIdentityMismatch, refused.Refusal);
        Assert.Equal(new ContentTypeId(requestedTypeId), refused.Type);
        Assert.Equal<long?>(storedTypeId, refused.StoredTypeId);
        Assert.Equal("thing", refused.StoredTypeKey);
        Assert.Equal(requestedKey, refused.RegisteredTypeKey);
        Assert.Equal(storedTypeId, database.Scalar("SELECT type_id FROM catalog_type WHERE type_key = 'thing';"));
        if (storedTypeId != 1024)
        {
            database.Execute("UPDATE catalog_type SET type_id = 1024 WHERE type_key = 'thing';");
        }
        using var repair = new SqliteContentAuthoringStore(database.ConnectionString, Registry());
        await repair.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
    }

    /// <summary>
    /// A catalog another connection holds locked reports the LOCK, not a schema to migrate. The refusal names a
    /// migration an operator would go and apply, so it is kept for a file that does not read as a catalog, and
    /// a lock, a full disk or an I/O failure reaches the host as the provider's own error.
    /// </summary>
    [Fact]
    public async Task ALockedDatabaseReportsTheLockRatherThanASchemaToMigrate()
    {
        using var database = new TemporaryCatalogDatabase();
        using (var created = new SqliteContentAuthoringStore(database.ConnectionString, Registry()))
        {
            await created.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        }

        using var holder = new SqliteConnection(database.ConnectionString + ";Pooling=False");
        holder.Open();
        using (SqliteCommand begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN EXCLUSIVE;";
            begin.ExecuteNonQuery();
        }

        using var store = new SqliteContentAuthoringStore(database.ConnectionString + ";Default Timeout=1", Registry());
        SqliteException busy = await Assert.ThrowsAsync<SqliteException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal(SQLitePCL.raw.SQLITE_BUSY, busy.SqliteErrorCode);
    }

    /// <summary>
    /// The other side of the line above: catalog tables with no metadata table are a file that does not read
    /// as a catalog, and that is still refused naming the migration.
    /// </summary>
    [Fact]
    public async Task ACatalogWithoutItsMetadataTableIsStillRefusedAsUnreadable()
    {
        using var database = new TemporaryCatalogDatabase();
        database.Execute("CREATE TABLE catalog_orphan (a INTEGER NOT NULL PRIMARY KEY);");

        using var store = new SqliteContentAuthoringStore(database.ConnectionString, Registry());
        ContentAuthoringException refused = await Assert.ThrowsAsync<ContentAuthoringException>(
            () => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));

        Assert.Equal("schema-mismatch", refused.Reason);
        Assert.Equal(ContentAuthoringRefusal.SchemaMetadataUnreadable, refused.Refusal);
        Assert.Equal(1, Assert.IsType<SqliteException>(refused.InnerException).SqliteErrorCode);
        Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
        database.Execute("DROP TABLE catalog_orphan;");
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
    }
}
