using System;
using System.Collections.Generic;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class SqliteJournalSchemaRefusalTests
{
    [Theory]
    [InlineData("DELETE FROM journal_metadata;", JournalStoreRefusal.SchemaMetadataMissing, null)]
    [InlineData("UPDATE journal_metadata SET schema_version = X'01';", JournalStoreRefusal.SchemaVersionMalformed, typeof(byte[]))]
    public void Metadata_faults_name_the_rule_and_open_after_repair(string fault, JournalStoreRefusal reason, Type? type)
    {
        using var database = new SqliteJournalTestDatabase();
        string path = database.NewPath();
        using (database.Open(path)) { }
        database.Execute(path, "CREATE TABLE saved_metadata AS SELECT * FROM journal_metadata; " + fault);
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);
        var refused = Assert.Throws<JournalStoreException>(() => database.Open(path));
        Assert.Equal(reason, refused.Reason);
        Assert.Equal(type, refused.MetadataType);
        Assert.Equal("sqlite-journal-v3-row-timestamps", refused.RequiredMigration);
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        database.Execute(path, "DELETE FROM journal_metadata; INSERT INTO journal_metadata SELECT * FROM saved_metadata; DROP TABLE saved_metadata;");
        using (database.Open(path)) { }
    }

    [Fact]
    public void Foreign_keys_must_be_enabled_on_the_held_connection()
    {
        using var database = new SqliteJournalTestDatabase();
        string path = database.NewPath();
        using (database.Open(path)) { }
        using var connection = new SqliteConnection(database.ConnectionString(path));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = OFF;";
        command.ExecuteNonQuery();
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);
        var refused = Assert.Throws<JournalStoreException>(() => SqliteJournalSchema.Initialize(connection, SqliteJournalSchemaMode.ValidateOnly));
        Assert.Equal(JournalStoreRefusal.SchemaForeignKeysDisabled, refused.Reason);
        Assert.Equal("sqlite-journal-v3-row-timestamps", refused.RequiredMigration);
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();
        SqliteJournalSchema.Initialize(connection, SqliteJournalSchemaMode.ValidateOnly);
    }

    [Theory]
    [InlineData(1, "DROP INDEX ix_journal_event_operation;", JournalStoreRefusal.SchemaObjectInventoryMismatch)]
    [InlineData(2, "DROP INDEX ix_journal_event_operation;", JournalStoreRefusal.SchemaObjectInventoryMismatch)]
    [InlineData(3, "CREATE TABLE journal_extra(value INTEGER);", JournalStoreRefusal.SchemaObjectInventoryMismatch)]
    [InlineData(1, "DROP INDEX ix_journal_event_operation; CREATE INDEX ix_journal_extra ON journal_event(operation_id);", JournalStoreRefusal.SchemaObjectMissing)]
    [InlineData(2, "DROP INDEX ix_journal_event_operation; CREATE INDEX ix_journal_extra ON journal_event(operation_id);", JournalStoreRefusal.SchemaObjectMissing)]
    [InlineData(3, "DROP INDEX ix_journal_event_operation; CREATE INDEX ix_journal_extra ON journal_event(operation_id);", JournalStoreRefusal.SchemaObjectMissing)]
    [InlineData(2, "DROP INDEX ix_journal_event_operation; CREATE INDEX ix_journal_event_operation ON journal_event(stream_version);", JournalStoreRefusal.SchemaObjectDefinitionMismatch)]
    public void Shape_faults_keep_the_compared_object_or_complete_inventory(int version, string fault, JournalStoreRefusal reason)
    {
        using var database = new SqliteJournalTestDatabase();
        string path = database.NewPath();
        if (version == 3) { using (database.Open(path)) { } }
        else database.Execute(path, version == 1 ? SqliteMutationJournalStore.VersionOneSchemaSqlForTest : SqliteJournalSchema.VersionTwoSchemaSqlForTest);
        database.Execute(path, fault);
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);
        var refused = Assert.Throws<JournalStoreException>(() => database.Open(path));
        Assert.Equal(reason, refused.Reason);
        Assert.Equal(version, refused.ExpectedVersion);
        Assert.Equal("sqlite-journal-v3-row-timestamps", refused.RequiredMigration);
        if (reason == JournalStoreRefusal.SchemaObjectInventoryMismatch)
        {
            Assert.Equal(version == 3 ? Array.Empty<string>() : new[] { "index:ix_journal_event_operation" }, refused.MissingObjects);
            Assert.Equal(version == 3 ? new[] { "table:journal_extra" } : Array.Empty<string>(), refused.UnexpectedObjects);
        }
        else Assert.Equal("index:ix_journal_event_operation", refused.ObjectName);
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        database.Execute(path, "DROP INDEX IF EXISTS ix_journal_extra; DROP TABLE IF EXISTS journal_extra; DROP INDEX IF EXISTS ix_journal_event_operation; CREATE INDEX ix_journal_event_operation ON journal_event(operation_id);");
        using (database.Open(path)) { }
    }

    [Fact]
    public void Schema_identifier_collections_own_their_storage()
    {
        long[] versions = [3];
        string[] missing = ["table:journal_stream"];
        string[] unexpected = ["table:journal_extra"];
        JournalHostObjectFinding[] objects = [new(JournalStoreRefusal.HostForeignKeyActionUnsafe, "game", "journal_stream", "CASCADE", null)];
        var refused = new JournalStoreException(JournalStoreRefusal.HostForeignKeyActionUnsafe, JournalStoreFailureKind.SchemaMismatch,
            JournalStoreFailureCertainty.DefinitelyNotCommitted, JournalStoreFailureScope.WholeStore, null, "test")
        { SupportedVersions = versions, MissingObjects = missing, UnexpectedObjects = unexpected, HostObjects = objects };
        versions[0] = 4;
        missing[0] = unexpected[0] = "changed";
        objects[0] = new(JournalStoreRefusal.HostTriggerUnsafe, null, "journal_stream", null, "trigger");
        Assert.Equal(new long[] { 3 }, refused.SupportedVersions);
        Assert.Equal(new[] { "table:journal_stream" }, refused.MissingObjects);
        Assert.Equal(new[] { "table:journal_extra" }, refused.UnexpectedObjects);
        Assert.Equal(new[] { new JournalHostObjectFinding(JournalStoreRefusal.HostForeignKeyActionUnsafe, "game", "journal_stream", "CASCADE", null) }, refused.HostObjects);
        AssertReadOnly(refused.SupportedVersions!, 4L);
        AssertReadOnly(refused.MissingObjects!, "changed");
        AssertReadOnly(refused.UnexpectedObjects!, "changed");
        AssertReadOnly(refused.HostObjects!, objects[0]);
    }

    private static void AssertReadOnly<T>(IReadOnlyList<T> values, T replacement)
    {
        Assert.Throws<NotSupportedException>(() => ((IList<T>)values)[0] = replacement);
        Assert.Throws<NotSupportedException>(() => ((System.Collections.ICollection)values).SyncRoot);
    }
}
