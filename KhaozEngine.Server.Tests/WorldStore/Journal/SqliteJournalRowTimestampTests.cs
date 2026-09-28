using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// Schema version 3 of the SQLite journal: every row records when it was created, a fresh store writes the time from
/// its own clock, and the version 2 to 3 migration fills a legacy row only from a time the database proves.
/// </summary>
public sealed class SqliteJournalRowTimestampTests : IDisposable
{
    private const string MigrationName = "sqlite-journal-v3-row-timestamps";

    private static readonly DateTimeOffset T0 = new(2026, 9, 6, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The four tables version 3 gives a <c>created_at_utc</c> column.</summary>
    private static readonly string[] CreationColumnTables =
    {
        "journal_metadata", "journal_stream", "journal_operation_stream", "journal_projection",
    };

    /// <summary>Every journal table and the column that holds its creation time.</summary>
    private static readonly (string Table, string Column)[] CreationTimes =
    {
        ("journal_event", "committed_at_utc"),
        ("journal_metadata", "created_at_utc"),
        ("journal_operation", "committed_at_utc"),
        ("journal_operation_stream", "created_at_utc"),
        ("journal_projection", "created_at_utc"),
        ("journal_snapshot", "created_at_utc"),
        ("journal_stream", "created_at_utc"),
    };

    private static readonly string LegacyInitialization = Guid.Parse("00000000-0000-0000-0000-00000000000a").ToString("D");
    private static readonly string LegacyCommit = Guid.Parse("00000000-0000-0000-0000-00000000000b").ToString("D");
    private static readonly string LegacyEmptyCommit = Guid.Parse("00000000-0000-0000-0000-00000000000c").ToString("D");

    private readonly SqliteJournalTestDatabase database = new();

    [Fact]
    public void Fresh_store_creates_version_3_with_nullable_creation_columns()
    {
        string path = database.NewPath();
        using (Open(path, new SqliteJournalManualTimeProvider(T0))) { }

        Assert.Equal(3, SchemaVersion(path));
        foreach (string table in CreationColumnTables)
        {
            Assert.Equal(1, database.ScalarLong(path, $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = 'created_at_utc';"));
            Assert.Equal("INTEGER", database.ScalarText(path, $"SELECT type FROM pragma_table_info('{table}') WHERE name = 'created_at_utc';"));
            Assert.Equal(0, database.ScalarLong(path, $"SELECT [notnull] FROM pragma_table_info('{table}') WHERE name = 'created_at_utc';"));
        }
    }

    [Fact]
    public async Task Every_row_written_carries_its_creation_time()
    {
        string path = database.NewPath();
        var clock = new SqliteJournalManualTimeProvider(T0);
        using SqliteMutationJournalStore store = Open(path, clock);
        long? metadataCreated = NullableLong(path, "SELECT created_at_utc FROM journal_metadata;");
        long? metadataUpdated = NullableLong(path, "SELECT updated_at_utc FROM journal_metadata;");

        await store.InitializeAsync(Initialization(1, "player/a"));
        await store.InitializeAsync(Initialization(2, "player/b", Projection("player/b", "profile", 1)));
        clock.Advance(TimeSpan.FromMinutes(1));
        long t1 = clock.GetUtcNow().ToUnixTimeMilliseconds();
        await store.CommitAsync(Commit(3, "player/a", 0, Projection("player/a", "bag", 2)));
        clock.Advance(TimeSpan.FromMinutes(1));
        long t2 = clock.GetUtcNow().ToUnixTimeMilliseconds();
        await store.CommitAsync(Commit(4, "player/a", 1, Projection("player/a", "bag", 3)));
        await store.CompactAsync(new JournalCompaction("player/a", 1, "player.v1", 1, new byte[] { 30 }, 1));
        await store.RotateStoreEpochAsync();
        long t0 = T0.ToUnixTimeMilliseconds();

        Assert.NotNull(metadataCreated);
        Assert.Equal(metadataUpdated, metadataCreated);
        Assert.Equal(metadataCreated, NullableLong(path, "SELECT created_at_utc FROM journal_metadata;"));
        Assert.Equal(t2, NullableLong(path, "SELECT updated_at_utc FROM journal_metadata;"));
        Assert.Equal((t0, t2), StreamTimes(path, "player/a"));
        Assert.Equal((t0, t0), StreamTimes(path, "player/b"));
        Assert.Equal((t1, t2), ProjectionTimes(path, "player/a", "bag"));
        Assert.Equal((t0, t0), ProjectionTimes(path, "player/b", "profile"));
        Assert.Equal(4, database.ScalarLong(path, "SELECT COUNT(*) FROM journal_operation_stream;"));
        Assert.Equal(4, database.ScalarLong(path, """
            SELECT COUNT(*) FROM journal_operation_stream s
            JOIN journal_operation o ON o.operation_id = s.operation_id
            WHERE s.created_at_utc = o.committed_at_utc;
            """));
        Assert.Equal(
            CreationTimes.Select(value => value.Table).OrderBy(value => value, StringComparer.Ordinal),
            JournalTables(path));
        foreach ((string table, string column) in CreationTimes)
        {
            Assert.True(database.ScalarLong(path, $"SELECT COUNT(*) FROM {table};") > 0, $"{table} has no row to prove");
            Assert.Equal(0, database.ScalarLong(path, $"SELECT COUNT(*) FROM {table} WHERE {column} IS NULL;"));
        }
    }

    [Fact]
    public async Task Version_2_database_migrates_and_backfills_exactly()
    {
        string path = database.NewPath();
        database.CreateEmpty(path);
        database.Execute(path, SqliteJournalSchema.VersionTwoSchemaSqlForTest);
        SeedLegacyRows(path);
        Assert.Equal(2, SchemaVersion(path));
        Assert.Equal(0, CreationColumnCount(path));

        var clock = new SqliteJournalManualTimeProvider(T0.AddHours(1));
        using SqliteMutationJournalStore store = Open(path, clock);

        Assert.Equal(3, SchemaVersion(path));
        AssertLegacyBackfill(path);

        await store.InitializeAsync(Initialization(1, "player/new"));
        Assert.Equal((T0.AddHours(1).ToUnixTimeMilliseconds(), T0.AddHours(1).ToUnixTimeMilliseconds()), StreamTimes(path, "player/new"));
    }

    [Fact]
    public void Version_1_database_chains_to_version_3()
    {
        string path = database.NewPath();
        database.CreateEmpty(path);
        database.Execute(path, SqliteMutationJournalStore.VersionOneSchemaSqlForTest);
        SeedLegacyRows(path);
        Assert.Equal(1, SchemaVersion(path));

        using (Open(path, new SqliteJournalManualTimeProvider(T0.AddHours(1)))) { }

        Assert.Equal(3, SchemaVersion(path));
        Assert.Equal(1, database.ScalarLong(path, "SELECT COUNT(*) FROM pragma_table_info('journal_operation') WHERE name = 'retention_started_at_utc';"));
        Assert.Equal(CreationColumnTables.Length, CreationColumnCount(path));
        AssertLegacyBackfill(path);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Half_migrated_database_finishes_on_reopen(int columnsAdded)
    {
        string path = database.NewPath();
        database.CreateEmpty(path);
        database.Execute(path, SqliteJournalSchema.VersionTwoSchemaSqlForTest);
        SeedLegacyRows(path);
        foreach (string add in SqliteJournalSchema.VersionThreeColumnAddsSqlForTest.Take(columnsAdded))
            database.Execute(path, add);
        Assert.Equal(2, SchemaVersion(path));
        Assert.Equal(columnsAdded, CreationColumnCount(path));

        using (Open(path, new SqliteJournalManualTimeProvider(T0.AddHours(1)))) { }

        Assert.Equal(3, SchemaVersion(path));
        AssertLegacyBackfill(path);
    }

    [Theory]
    [InlineData(SqliteJournalSchemaMode.ValidateOnly)]
    [InlineData(SqliteJournalSchemaMode.ReadOnly)]
    public void ValidateOnly_and_ReadOnly_refuse_version_2_naming_the_migration(SqliteJournalSchemaMode mode)
    {
        string path = database.NewPath();
        database.CreateEmpty(path);
        database.Execute(path, SqliteJournalSchema.VersionTwoSchemaSqlForTest);
        SeedLegacyRows(path);
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);

        JournalStoreException refused = Assert.Throws<JournalStoreException>(() => database.Open(
            path,
            new SqliteMutationJournalStoreOptions(database.ConnectionString(path)) { SchemaMode = mode }));

        Assert.Equal(JournalStoreFailureKind.SchemaMismatch, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
        Assert.Contains("unsupported version '2'", refused.Message, StringComparison.Ordinal);
        Assert.Contains(MigrationName, refused.Message, StringComparison.Ordinal);
        Assert.Equal(MigrationName, SqliteJournalSchema.RequiredMigration);
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
    }

    /// <summary>
    /// The legacy rows <see cref="SeedLegacyRows"/> wrote, after a migration. Stream A kept its initialization
    /// snapshot, so its creation time is that snapshot's. Stream B's snapshot was replaced, so its creation time is
    /// unknown even though a retained operation looks like an initialization. Receipt stream ranges take their
    /// operation's commit time, and nothing else is guessed.
    /// </summary>
    private void AssertLegacyBackfill(string path)
    {
        long t0 = T0.ToUnixTimeMilliseconds();
        Assert.Equal((t0, T0.AddMinutes(3).ToUnixTimeMilliseconds()), StreamTimes(path, "legacy/a"));
        Assert.Equal(((long?)null, T0.AddMinutes(20).ToUnixTimeMilliseconds()), StreamTimes(path, "legacy/b"));
        Assert.Equal(t0, NullableLong(path, $"SELECT created_at_utc FROM journal_operation_stream WHERE operation_id = '{LegacyInitialization}';"));
        Assert.Equal(T0.AddMinutes(3).ToUnixTimeMilliseconds(), NullableLong(path, $"SELECT created_at_utc FROM journal_operation_stream WHERE operation_id = '{LegacyCommit}';"));
        Assert.Equal(T0.AddMinutes(10).ToUnixTimeMilliseconds(), NullableLong(path, $"SELECT created_at_utc FROM journal_operation_stream WHERE operation_id = '{LegacyEmptyCommit}';"));
        Assert.Equal(((long?)null, T0.AddMinutes(3).ToUnixTimeMilliseconds()), ProjectionTimes(path, "legacy/a", "bag"));
        Assert.Null(NullableLong(path, "SELECT created_at_utc FROM journal_metadata;"));
    }

    /// <summary>
    /// Rows written in the version 1 column set, which a version 2 table also accepts. Stream A still holds its
    /// initialization snapshot at version 0. Stream B was compacted to version 5, and its only retained operation
    /// has the empty shape an initialization has, at a later time.
    /// </summary>
    private void SeedLegacyRows(string path)
    {
        database.Execute(
            path,
            """
            INSERT INTO journal_stream(stream_key, current_version, retained_floor, updated_at_utc)
            VALUES ('legacy/a', 1, 0, $t3), ('legacy/b', 5, 5, $t20);
            INSERT INTO journal_snapshot(
                stream_key, through_version, snapshot_schema, snapshot_schema_version, data, data_sha256, created_at_utc)
            VALUES ('legacy/a', 0, 'player.v1', 1, X'', zeroblob(32), $t0),
                   ('legacy/b', 5, 'player.v1', 1, X'05', zeroblob(32), $t20);
            INSERT INTO journal_event(
                stream_key, stream_version, operation_id, operation_ordinal, event_type,
                event_schema_version, payload, payload_sha256, committed_at_utc)
            VALUES ('legacy/a', 1, $commit, 0, 'state.changed', 1, X'01', zeroblob(32), $t3);
            INSERT INTO journal_operation(
                operation_id, operation_kind, intent_fingerprint_format, intent_fingerprint,
                execution_fingerprint_format, execution_fingerprint, result_schema,
                result_schema_version, result_data, result_sha256, committed_at_utc)
            VALUES ($initialization, 'bank.deposit', 1, zeroblob(32), 1, zeroblob(32), 'result.v1', 1, X'', zeroblob(32), $t0),
                   ($commit, 'bank.deposit', 1, zeroblob(32), 1, zeroblob(32), 'result.v1', 1, X'', zeroblob(32), $t3),
                   ($empty, 'bank.deposit', 1, zeroblob(32), 1, zeroblob(32), 'result.v1', 1, X'', zeroblob(32), $t10);
            INSERT INTO journal_operation_stream(operation_id, stream_key, before_version, after_version, event_count)
            VALUES ($initialization, 'legacy/a', 0, 0, 0),
                   ($commit, 'legacy/a', 0, 1, 1),
                   ($empty, 'legacy/b', 0, 0, 0);
            INSERT INTO journal_projection(
                stream_key, section_name, source_version, projection_schema,
                projection_schema_version, data, data_sha256, updated_at_utc)
            VALUES ('legacy/a', 'bag', 1, 'section.v1', 1, X'09', zeroblob(32), $t3);
            """,
            ("$t0", T0.ToUnixTimeMilliseconds()),
            ("$t3", T0.AddMinutes(3).ToUnixTimeMilliseconds()),
            ("$t10", T0.AddMinutes(10).ToUnixTimeMilliseconds()),
            ("$t20", T0.AddMinutes(20).ToUnixTimeMilliseconds()),
            ("$initialization", LegacyInitialization),
            ("$commit", LegacyCommit),
            ("$empty", LegacyEmptyCommit));
    }

    private SqliteMutationJournalStore Open(string path, TimeProvider clock)
        => database.Open(path, new SqliteMutationJournalStoreOptions(database.ConnectionString(path)) { TimeProvider = clock });

    private long SchemaVersion(string path)
        => database.ScalarLong(path, "SELECT schema_version FROM journal_metadata WHERE metadata_key = 1;");

    private long CreationColumnCount(string path)
        => CreationColumnTables.Sum(table => database.ScalarLong(
            path,
            $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = 'created_at_utc';"));

    private (long? Created, long? Updated) StreamTimes(string path, string streamKey)
        => (NullableLong(path, $"SELECT created_at_utc FROM journal_stream WHERE stream_key = '{streamKey}';"),
            NullableLong(path, $"SELECT updated_at_utc FROM journal_stream WHERE stream_key = '{streamKey}';"));

    private (long? Created, long? Updated) ProjectionTimes(string path, string streamKey, string section)
        => (NullableLong(path, $"SELECT created_at_utc FROM journal_projection WHERE stream_key = '{streamKey}' AND section_name = '{section}';"),
            NullableLong(path, $"SELECT updated_at_utc FROM journal_projection WHERE stream_key = '{streamKey}' AND section_name = '{section}';"));

    private IReadOnlyList<string> JournalTables(string path)
    {
        using var connection = new SqliteConnection(database.ConnectionString(path));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE 'journal\\_%' ESCAPE '\\' ORDER BY name;";
        using SqliteDataReader reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read()) tables.Add(reader.GetString(0));
        return tables;
    }

    private long? NullableLong(string path, string sql)
    {
        using var connection = new SqliteConnection(database.ConnectionString(path));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private static JournalOperationIdentity Identity(int suffix)
        => new(new Guid(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, checked((byte)suffix)), "world/account", "bank.deposit", new[] { checked((byte)suffix) });

    private static JournalInitialization Initialization(int suffix, string streamKey, params JournalProjectionWrite[] projections)
        => new(Identity(suffix), streamKey, "player.v1", 1, Array.Empty<byte>(), projections, "result.v1", 1, new byte[] { 1 });

    private static JournalCommit Commit(int suffix, string streamKey, long expectedVersion, JournalProjectionWrite projection)
        => new(
            Identity(suffix),
            new[] { new JournalStreamMutation(streamKey, expectedVersion, new[] { new JournalEvent("state.changed", 1, new[] { checked((byte)suffix) }) }) },
            new[] { projection },
            "result.v1",
            1,
            new byte[] { 1 });

    private static JournalProjectionWrite Projection(string streamKey, string section, byte value)
        => new(streamKey, section, "section.v1", 1, new[] { value });

    public void Dispose() => database.Dispose();
}
