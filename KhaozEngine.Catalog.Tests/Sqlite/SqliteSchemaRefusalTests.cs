using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Catalog.Authoring;
using KhaozEngine.Catalog.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.Catalog.Sqlite;

public class SqliteSchemaRefusalTests
{
    [Theory]
    [InlineData("DELETE FROM catalog_metadata;", ContentAuthoringRefusal.SchemaValidationMetadataMissing, null)]
    [InlineData("UPDATE catalog_metadata SET schema_version = 'bad';", ContentAuthoringRefusal.SchemaVersionMalformed, typeof(string))]
    [InlineData("UPDATE catalog_metadata SET schema_version = 5;", ContentAuthoringRefusal.SchemaVersionUnsupported, null)]
    public async Task MetadataFaultsNameTheirRuleAndOpenAfterRepair(string fault, ContentAuthoringRefusal reason, Type? metadataType)
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, SqliteCatalogResetHarness.Registry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        database.Execute("CREATE TABLE saved_metadata AS SELECT * FROM catalog_metadata; PRAGMA ignore_check_constraints = ON; " + fault);
        string before = SqliteCatalogNonmutatingOpenTests.Snapshot(database.ConnectionString);
        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly));
        Assert.Equal(reason, refused.Refusal);
        Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
        Assert.Equal(metadataType, refused.MetadataType);
        if (reason == ContentAuthoringRefusal.SchemaVersionUnsupported)
        {
            Assert.Equal(5L, refused.ActualVersion);
            Assert.Equal(new long[] { 4 }, refused.SupportedVersions);
        }
        Assert.Equal(before, SqliteCatalogNonmutatingOpenTests.Snapshot(database.ConnectionString));
        database.Execute("DELETE FROM catalog_metadata; INSERT INTO catalog_metadata SELECT * FROM saved_metadata; DROP TABLE saved_metadata;");
        await store.InitializeAsync(ContentAuthoringSchemaMode.ValidateOnly);
    }

    [Fact]
    public void ForeignKeysMustBeEnabledOnTheHeldConnection()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SqliteCatalogSchema.Tables + "PRAGMA foreign_keys = OFF;";
        command.ExecuteNonQuery();
        var refused = Assert.Throws<ContentAuthoringException>(() => SqliteCatalogSchemaValidation.Initialize(connection, ContentAuthoringSchemaMode.ValidateOnly));
        Assert.Equal(ContentAuthoringRefusal.SchemaForeignKeysDisabled, refused.Refusal);
        Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        SqliteCatalogSchemaValidation.Initialize(connection, ContentAuthoringSchemaMode.ValidateOnly);
    }

    [Theory]
    [InlineData("DROP INDEX ix_catalog_row_live;", ContentAuthoringRefusal.SchemaObjectMissing, "index:ix_catalog_row_live")]
    [InlineData("DROP INDEX ix_catalog_row_live; CREATE INDEX ix_catalog_row_live ON catalog_row(definition_id);", ContentAuthoringRefusal.SchemaObjectDefinitionMismatch, "index:ix_catalog_row_live")]
    [InlineData("CREATE TABLE catalog_extra(a INTEGER);", ContentAuthoringRefusal.SchemaObjectUnexpected, "table:catalog_extra")]
    public void VersionTwoShapeFaultsNameTheComparedObject(string fault, ContentAuthoringRefusal reason, string name)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SqliteCatalogSchema.VersionTwoTables + fault;
        command.ExecuteNonQuery();
        var refused = Assert.Throws<ContentAuthoringException>(() => SqliteCatalogSchemaValidation.ValidateVersionTwoObjects(connection));
        Assert.Equal(reason, refused.Refusal);
        Assert.Equal(name, refused.ObjectName);
        Assert.Equal(2, refused.ExpectedVersion);
        Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
        command.CommandText = "DROP INDEX IF EXISTS ix_catalog_row_live; CREATE INDEX ix_catalog_row_live ON catalog_row(type_id, replaced_in_version, definition_id); DROP TABLE IF EXISTS catalog_extra;";
        command.ExecuteNonQuery();
        Assert.Equal(2L, SqliteCatalogSchemaValidation.ValidateVersionTwoObjects(connection));
    }

    [Theory]
    [InlineData("ALTER TABLE catalog_metadata DROP COLUMN active_version;", ContentAuthoringRefusal.SchemaMetadataUnreadable)]
    [InlineData("UPDATE catalog_metadata SET active_version = X'01';", ContentAuthoringRefusal.StoredScalarTypeMismatch)]
    [InlineData("CREATE TABLE saved_metadata AS SELECT * FROM catalog_metadata; DROP TABLE catalog_metadata; CREATE TABLE catalog_metadata AS SELECT metadata_key, schema_version, store_epoch, NULL AS active_version FROM saved_metadata; DROP TABLE saved_metadata;", ContentAuthoringRefusal.SchemaValidationMetadataMissing)]
    public async Task ResetRefusesMalformedWholeCatalogsWithoutChangingContent(string fault, ContentAuthoringRefusal reason)
    {
        using var database = new TemporaryCatalogDatabase();
        using var store = new SqliteContentAuthoringStore(database.ConnectionString, SqliteCatalogResetHarness.Registry());
        await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
        database.Execute("PRAGMA ignore_check_constraints = ON; " + fault);
        string before = SqliteCatalogNonmutatingOpenTests.Snapshot(database.ConnectionString);
        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() => SqliteCatalogReset.ResetAsync(database.ConnectionString, "actor", "", ""));
        Assert.Equal(reason, refused.Refusal);
        Assert.Equal(before, SqliteCatalogNonmutatingOpenTests.Snapshot(database.ConnectionString));
        if (reason == ContentAuthoringRefusal.StoredScalarTypeMismatch)
        {
            Assert.Equal(typeof(long), refused.ExpectedScalarType);
            Assert.Equal(typeof(byte[]), refused.ActualScalarType);
            database.Execute("UPDATE catalog_metadata SET active_version = 0;");
        }
        else
        {
            Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
            if (reason == ContentAuthoringRefusal.SchemaMetadataUnreadable)
            {
                Assert.Equal(1, Assert.IsType<SqliteException>(refused.InnerException).SqliteErrorCode);
                database.Execute("ALTER TABLE catalog_metadata ADD COLUMN active_version INTEGER NOT NULL DEFAULT 0;");
            }
            else database.Execute("UPDATE catalog_metadata SET active_version = 0;");
        }
        await SqliteCatalogReset.ResetAsync(database.ConnectionString, "actor", "", "");
    }

    [Fact]
    public void SchemaIdentifierCollectionsOwnTheirStorage()
    {
        long[] versions = [4];
        ContentHostForeignKeyFinding[] keys = [new("host", "catalog_version", "CASCADE")];
        var refusal = new ContentAuthoringException(ContentAuthoringRefusal.HostForeignKeyActionUnsafe, "test", default, 0, ContentAuthoringException.HostForeignKeyReason)
        { SupportedVersions = versions, HostObjects = keys };
        versions[0] = 5;
        keys[0] = new("changed", "catalog_row", "SET NULL");
        Assert.Equal(new long[] { 4 }, refusal.SupportedVersions);
        Assert.Equal(new[] { new ContentHostForeignKeyFinding("host", "catalog_version", "CASCADE") }, refusal.HostObjects);
        Assert.Throws<NotSupportedException>(() => ((IList<long>)refusal.SupportedVersions!)[0] = 6);
        Assert.Throws<NotSupportedException>(() => ((IList<ContentHostForeignKeyFinding>)refusal.HostObjects!)[0] = keys[0]);
        Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)refusal.SupportedVersions!).SyncRoot);
        Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)refusal.HostObjects!).SyncRoot);
    }

    [Theory]
    [InlineData("NULL", ContentAuthoringRefusal.SchemaValidationMetadataMissing, null)]
    [InlineData("X'01'", ContentAuthoringRefusal.StoredScalarTypeMismatch, typeof(byte[]))]
    public async Task AViewThatInterceptsRecreationRefusesItsEpochAndRollsBackItsTriggerWrite(
        string epoch, ContentAuthoringRefusal reason, Type? scalarType)
    {
        using var database = new TemporaryCatalogDatabase();
        using (var store = new SqliteContentAuthoringStore(database.ConnectionString,
            SqliteCatalogResetHarness.Registry(), database.Pack()))
        {
            await store.InitializeAsync(ContentAuthoringSchemaMode.AutoCreate);
            await SqliteCatalogResetHarness.Seed(store, "one", "two");
        }
        // Reset inventories tables. A view and its INSTEAD OF trigger survive those drops.
        // The trigger's side write makes the rollback assertion fail if it escapes the transaction.
        database.Execute(FormattableString.Invariant($"""
            DROP TABLE catalog_metadata;
            CREATE TABLE host_epoch(store_epoch);
            INSERT INTO host_epoch VALUES ({epoch});
            CREATE TABLE host_recreate_probe(value TEXT NOT NULL);
            CREATE VIEW catalog_metadata AS SELECT
                1 AS metadata_key, 4 AS schema_version, store_epoch, 0 AS active_version,
                NULL AS pinned_version, 0 AS updated_at_utc, NULL AS created_at_utc FROM host_epoch;
            CREATE TRIGGER host_metadata_insert INSTEAD OF INSERT ON catalog_metadata BEGIN
                INSERT INTO host_recreate_probe VALUES ('recreate wrote');
            END;
            """));
        string before = SqliteCatalogNonmutatingOpenTests.Snapshot(database.ConnectionString);
        var refused = await Assert.ThrowsAsync<ContentAuthoringException>(() =>
            SqliteCatalogReset.ResetAsync(database.ConnectionString, "actor", "", "", force: true));
        Assert.Equal(reason, refused.Refusal);
        Assert.Equal("schema-mismatch", refused.Reason);
        if (scalarType is null) Assert.Equal("catalog-v4-text-authoring", refused.RequiredMigration);
        else
        {
            Assert.Equal(typeof(string), refused.ExpectedScalarType);
            Assert.Equal(scalarType, refused.ActualScalarType);
        }
        Assert.Equal(before, SqliteCatalogNonmutatingOpenTests.Snapshot(database.ConnectionString));
        Assert.Equal(0, database.Scalar("SELECT COUNT(*) FROM host_recreate_probe;"));
        Assert.Equal(1, database.Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type = 'trigger' AND name = 'host_metadata_insert';"));
        database.Execute("DROP VIEW catalog_metadata;");
        var reset = await SqliteCatalogReset.ResetAsync(database.ConnectionString, "actor", "", "", force: true);
        Assert.Equal(4, reset.SchemaVersion);
        Assert.Equal(32, reset.StoreEpoch.Length);
    }
}
