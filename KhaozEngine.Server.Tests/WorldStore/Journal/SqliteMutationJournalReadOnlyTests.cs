using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class SqliteMutationJournalReadOnlyTests : IDisposable
{
    private readonly SqliteJournalTestDatabase database = new();

    [Fact]
    public async Task Read_only_store_lists_and_reads_without_changing_the_database_file()
    {
        string path = await SeedAsync();
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);

        using (SqliteMutationJournalStore store = OpenReadOnly(path))
        {
            JournalStreamPage page = await store.ListStreamsAsync(new JournalStreamQuery(10));
            JournalSnapshot snapshot = (await store.LoadSnapshotAsync("player/a"))!;
            JournalEventPage events = await store.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024));
            JournalProjectionRead projections = await store.ReadProjectionsAsync(new JournalProjectionQuery("player/a"));
            JournalOperationResolution resolution = await store.ResolveOperationAsync(Identity(3));

            Assert.Equal(new[] { ("player/a", 2L), ("player/b", 0L) }, page.Streams.Select(value => (value.StreamKey, value.HeadVersion)));
            Assert.Equal(1, snapshot.ThroughVersion);
            Assert.Equal(new byte[] { 30 }, snapshot.Data.ToArray());
            Assert.Equal(new long[] { 1, 2 }, events.Events.Select(value => value.StreamVersion));
            Assert.Equal(new byte[] { 9 }, Assert.Single(projections.Sections).Data.ToArray());
            Assert.Equal(JournalOperationResolutionStatus.Replayed, resolution.Status);
        }

        Assert.Equal(before, SqliteJournalFileState.Capture(path));
    }

    [Fact]
    public async Task Read_only_store_refuses_every_write_path_and_leaves_the_file_unchanged()
    {
        string path = await SeedAsync();
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);

        using (SqliteMutationJournalStore store = OpenReadOnly(path))
        {
            await AssertRefused(nameof(store.InitializeAsync), () => store.InitializeAsync(Initialization(10, "player/c")));
            await AssertRefused(nameof(store.CommitAsync), () => store.CommitAsync(Commit(11, "player/a", 2)));
            await AssertRefused(nameof(store.CompactAsync), () => store.CompactAsync(new JournalCompaction("player/a", 2, "player.v2", 2, new byte[] { 40 }, 2)));
            await AssertRefused(nameof(store.PurgeOperationsAsync), () => store.PurgeOperationsAsync(new JournalOperationPurge(DateTimeOffset.MaxValue, 10)));
            await AssertRefused(nameof(store.PurgeOperationsByAgeAsync), () => store.PurgeOperationsByAgeAsync(new JournalOperationAgePurge(TimeSpan.Zero, 10)));
            await AssertRefused(nameof(store.RotateStoreEpochAsync), () => store.RotateStoreEpochAsync());
            Assert.Equal(2, (await store.ListStreamsAsync(new JournalStreamQuery(10))).Streams.Count);
        }

        Assert.Equal(before, SqliteJournalFileState.Capture(path));
    }

    [Fact]
    public async Task Read_only_store_holds_a_connection_sqlite_itself_opened_read_only()
    {
        string path = await SeedAsync();
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);
        string readWrite = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();

        using (SqliteMutationJournalStore store = database.Open(
            path,
            new SqliteMutationJournalStoreOptions(readWrite) { SchemaMode = SqliteJournalSchemaMode.ReadOnly }))
        {
            SqliteException ddl = await Assert.ThrowsAsync<SqliteException>(
                () => store.ExecuteOnHeldConnectionForTestAsync("CREATE TABLE probe (value INTEGER);"));
            SqliteException dml = await Assert.ThrowsAsync<SqliteException>(
                () => store.ExecuteOnHeldConnectionForTestAsync("DELETE FROM journal_stream;"));

            Assert.Equal(SqliteReadOnlyErrorCode, ddl.SqliteErrorCode);
            Assert.Equal(SqliteReadOnlyErrorCode, dml.SqliteErrorCode);
        }

        Assert.Equal(before, SqliteJournalFileState.Capture(path));
    }

    [Fact]
    public void Read_only_open_of_a_database_with_no_journal_schema_is_refused_and_creates_nothing()
    {
        string path = database.NewPath();
        database.Execute(path, "CREATE TABLE unrelated (value INTEGER); INSERT INTO unrelated VALUES (1);");
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);

        JournalStoreException exception = Assert.Throws<JournalStoreException>(() => OpenReadOnly(path));

        AssertSchemaRefusal(exception);
        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
    }

    [Fact]
    public void Read_only_open_of_a_missing_file_is_refused_and_creates_no_file()
    {
        string path = database.NewPath();

        JournalStoreException exception = Assert.Throws<JournalStoreException>(() => OpenReadOnly(path));

        Assert.Equal(JournalStoreFailureKind.Unavailable, exception.Kind);
        Assert.Equal(JournalStoreFailureScope.WholeStore, exception.Scope);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Read_only_open_of_a_version_one_journal_is_refused_and_not_migrated()
    {
        string path = database.NewPath();
        database.CreateEmpty(path);
        database.Execute(path, SqliteMutationJournalStore.VersionOneSchemaSqlForTest);
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);

        JournalStoreException exception = Assert.Throws<JournalStoreException>(() => OpenReadOnly(path));

        AssertSchemaRefusal(exception);
        Assert.Contains("unsupported version '1'", exception.Message, StringComparison.Ordinal);
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        Assert.Equal(1, database.ScalarLong(path, "SELECT schema_version FROM journal_metadata WHERE metadata_key = 1;"));
    }

    [Fact]
    public async Task Read_only_reads_a_journal_that_is_not_in_wal_mode_without_changing_it()
    {
        string path = await SeedAsync();
        database.Execute(path, "PRAGMA journal_mode = DELETE;");
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);

        using (SqliteMutationJournalStore store = OpenReadOnly(path))
            Assert.Equal(2, (await store.ListStreamsAsync(new JournalStreamQuery(10))).Streams.Count);

        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        Assert.Equal("delete", database.ScalarText(path, "PRAGMA journal_mode;"));
    }

    private const int SqliteReadOnlyErrorCode = 8;

    private async Task<string> SeedAsync()
    {
        string path = database.NewPath();
        using (SqliteMutationJournalStore writer = database.Open(path))
        {
            await writer.InitializeAsync(Initialization(1, "player/a", new JournalProjectionWrite("player/a", "bag", "bag.v1", 1, new byte[] { 5 })));
            await writer.InitializeAsync(Initialization(2, "player/b"));
            await writer.CommitAsync(new JournalCommit(
                Identity(3),
                new[] { new JournalStreamMutation("player/a", 0, new[] { Event(1), Event(2) }) },
                new[] { new JournalProjectionWrite("player/a", "bag", "bag.v1", 1, new byte[] { 9 }) },
                "result.v1",
                1,
                new byte[] { 3 }));
            await writer.CompactAsync(new JournalCompaction("player/a", 1, "player.v1", 1, new byte[] { 30 }, null));
        }
        return path;
    }

    private SqliteMutationJournalStore OpenReadOnly(string path)
        => database.Open(path, new SqliteMutationJournalStoreOptions(database.ConnectionString(path))
        {
            SchemaMode = SqliteJournalSchemaMode.ReadOnly,
        });

    private static async Task AssertRefused(string operation, Func<Task> write)
    {
        NotSupportedException exception = await Assert.ThrowsAsync<NotSupportedException>(write);
        Assert.Contains(nameof(SqliteJournalSchemaMode.ReadOnly), exception.Message, StringComparison.Ordinal);
        Assert.Contains(operation, exception.Message, StringComparison.Ordinal);
    }

    private static void AssertSchemaRefusal(JournalStoreException exception)
    {
        Assert.Equal(JournalStoreFailureKind.SchemaMismatch, exception.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, exception.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, exception.Scope);
        Assert.Contains(SqliteJournalSchema.RequiredMigration, exception.Message, StringComparison.Ordinal);
    }

    private static JournalOperationIdentity Identity(int suffix)
        => new(new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, checked((byte)suffix)), "world/account", "bank.deposit", new[] { checked((byte)suffix) });

    private static JournalInitialization Initialization(int suffix, string streamKey, params JournalProjectionWrite[] projections)
        => new(Identity(suffix), streamKey, "player.v1", 1, Array.Empty<byte>(), projections, "result.v1", 1, new byte[] { 1 });

    private static JournalCommit Commit(int suffix, string streamKey, long expectedVersion)
        => new(
            Identity(suffix),
            new[] { new JournalStreamMutation(streamKey, expectedVersion, new[] { Event(7) }) },
            Array.Empty<JournalProjectionWrite>(),
            "result.v1",
            1,
            new byte[] { 1 });

    private static JournalEvent Event(byte value) => new("state.changed", 1, new[] { value });

    public void Dispose() => database.Dispose();
}

/// <summary>Everything a read only open must leave alone: the database file's bytes, its schema, and every row. The
/// dumps read through their own read only connection, so capturing a state cannot change it.</summary>
internal sealed record SqliteJournalFileState(string Sha256, string Schema, string Rows)
{
    internal static SqliteJournalFileState Capture(string path)
    {
        string sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        string schema = Dump(connection, "SELECT type, name, tbl_name, sql FROM sqlite_master ORDER BY type, name;");
        var rows = new StringBuilder();
        foreach (string table in Tables(connection))
            rows.Append(table).Append('\n').Append(Dump(connection, $"SELECT * FROM \"{table}\" ORDER BY rowid;"));
        return new SqliteJournalFileState(sha256, schema, rows.ToString());
    }

    private static IReadOnlyList<string> Tables(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND substr(name, 1, 7) <> 'sqlite_' ORDER BY name;";
        using SqliteDataReader reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read()) tables.Add(reader.GetString(0));
        return tables;
    }

    private static string Dump(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        using SqliteDataReader reader = command.ExecuteReader();
        var text = new StringBuilder();
        while (reader.Read())
        {
            for (int i = 0; i < reader.FieldCount; i++)
            {
                object value = reader.GetValue(i);
                text.Append(value is byte[] bytes ? Convert.ToHexString(bytes) : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture));
                text.Append('|');
            }
            text.Append('\n');
        }
        return text.ToString();
    }
}
