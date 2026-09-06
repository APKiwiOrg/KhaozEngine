using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

public sealed class SqliteMutationJournalStoreTests : MutationJournalStoreConformance, IDisposable
{
    private readonly SqliteJournalTestDatabase database = new();

    protected override MutationJournalStoreHarness CreateStore(TimeSpan? minimumRetryHorizon = null)
    {
        string path = database.NewPath();
        var clock = new SqliteJournalManualTimeProvider(new DateTimeOffset(2026, 9, 6, 0, 0, 0, TimeSpan.Zero));
        SqliteMutationJournalStore store = database.Open(
            path,
            new SqliteMutationJournalStoreOptions(database.ConnectionString(path))
            {
                MinimumRetryHorizon = minimumRetryHorizon ?? TimeSpan.FromHours(24),
                TimeProvider = clock,
            });
        return new MutationJournalStoreHarness(store, store, clock.GetUtcNow, clock.Advance, operationId =>
            database.ExecuteAsync(path, "UPDATE journal_operation SET result_sha256 = zeroblob(32) WHERE operation_id = $id;", ("$id", operationId.ToString("D"))));
    }

    [Fact]
    public async Task Reopen_preserves_events_projections_snapshots_and_replay_receipts()
    {
        string path = database.NewPath();
        JournalOperationIdentity initializationIdentity = Operation(1);
        JournalOperationIdentity commitIdentity = Operation(2);
        JournalCommit commit = Commit(
            commitIdentity,
            Mutation("player/a", 0, Event(7)),
            new[] { Projection("player/a", "bag", 9) },
            Bytes(41));

        using (SqliteMutationJournalStore first = database.Open(path))
        {
            await first.InitializeAsync(Initialization(initializationIdentity, "player/a", Bytes(3)));
            await first.CommitAsync(commit);
        }

        using SqliteMutationJournalStore reopened = database.Open(
            path,
            new SqliteMutationJournalStoreOptions(database.ConnectionString(path))
            {
                SchemaMode = SqliteJournalSchemaMode.ValidateOnly,
            });
        JournalOperationResolution replay = await reopened.ResolveOperationAsync(commitIdentity);
        JournalSnapshot snapshot = (await reopened.LoadSnapshotAsync("player/a"))!;
        JournalStoredEvent storedEvent = Assert.Single((await reopened.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
        JournalProjectionSection projection = Assert.Single((await reopened.ReadProjectionsAsync(new JournalProjectionQuery("player/a"))).Sections);

        Assert.Equal(JournalOperationResolutionStatus.Replayed, replay.Status);
        Assert.Equal(Bytes(41), replay.Receipt!.ResultData.ToArray());
        Assert.Equal(Bytes(3), snapshot.Data.ToArray());
        Assert.Equal(Bytes(7), storedEvent.Payload.ToArray());
        Assert.Equal(Bytes(9), projection.Data.ToArray());
    }

    [Fact]
    public async Task Separate_store_connections_linearize_writers_against_one_head()
    {
        string path = database.NewPath();
        using SqliteMutationJournalStore first = database.Open(path);
        using SqliteMutationJournalStore second = database.Open(path);
        await first.InitializeAsync(Initialization(Operation(1), "player/a"));

        JournalCommitResult[] results = await Task.WhenAll(
            first.CommitAsync(Commit(Operation(2), Mutation("player/a", 0, Event(2)))),
            second.CommitAsync(Commit(Operation(3), Mutation("player/a", 0, Event(3)))));

        Assert.Single(results, value => value.Status == JournalCommitStatus.Applied);
        Assert.Single(results, value => value.Status == JournalCommitStatus.VersionConflict);
        Assert.Single((await first.ReadEventsAsync(new JournalEventRead("player/a", 0, null, 10, 1024))).Events);
    }

    [Fact]
    public async Task Stream_and_section_identity_is_ordinal_and_case_sensitive()
    {
        string path = database.NewPath();
        using SqliteMutationJournalStore store = database.Open(path);

        await store.InitializeAsync(Initialization(Operation(1), "player/A", projections: new[] { Projection("player/A", "Bag", 1) }));
        await store.InitializeAsync(Initialization(Operation(2), "player/a", projections: new[] { Projection("player/a", "bag", 2) }));

        Assert.Equal(Bytes(1), Assert.Single((await store.ReadProjectionsAsync(new JournalProjectionQuery("player/A"))).Sections).Data.ToArray());
        Assert.Equal(Bytes(2), Assert.Single((await store.ReadProjectionsAsync(new JournalProjectionQuery("player/a"))).Sections).Data.ToArray());
    }

    [Fact]
    public void Auto_create_is_idempotent_and_validate_only_accepts_version_one()
    {
        string path = database.NewPath();
        using (database.Open(path)) { }
        using (database.Open(path)) { }
        using (database.Open(
            path,
            new SqliteMutationJournalStoreOptions(database.ConnectionString(path))
            {
                SchemaMode = SqliteJournalSchemaMode.ValidateOnly,
            })) { }

        long tableCount = database.ScalarLong(
            path,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE 'journal_%';");
        long schemaVersion = database.ScalarLong(path, "SELECT schema_version FROM journal_metadata WHERE metadata_key = 1;");

        Assert.Equal(7, tableCount);
        Assert.Equal(1, schemaVersion);
    }

    [Fact]
    public void Validate_only_rejects_missing_and_unsupported_schemas_with_named_migration()
    {
        string missingPath = database.NewPath();
        database.CreateEmpty(missingPath);
        JournalStoreException missing = Assert.Throws<JournalStoreException>(() => database.Open(
            missingPath,
            new SqliteMutationJournalStoreOptions(database.ConnectionString(missingPath))
            {
                SchemaMode = SqliteJournalSchemaMode.ValidateOnly,
            }));

        string futurePath = database.NewPath();
        using (database.Open(futurePath)) { }
        database.Execute(futurePath, "UPDATE journal_metadata SET schema_version = 99 WHERE metadata_key = 1;");
        JournalStoreException future = Assert.Throws<JournalStoreException>(() => database.Open(
            futurePath,
            new SqliteMutationJournalStoreOptions(database.ConnectionString(futurePath))
            {
                SchemaMode = SqliteJournalSchemaMode.ValidateOnly,
            }));

        Assert.Equal((JournalStoreFailureKind.SchemaMismatch, JournalStoreFailureCertainty.DefinitelyNotCommitted), (missing.Kind, missing.Certainty));
        Assert.Equal((JournalStoreFailureKind.SchemaMismatch, JournalStoreFailureCertainty.DefinitelyNotCommitted), (future.Kind, future.Certainty));
        Assert.Contains(SqliteJournalSchema.RequiredMigration, missing.Message, StringComparison.Ordinal);
        Assert.Contains(SqliteJournalSchema.RequiredMigration, future.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Auto_create_does_not_modify_a_database_with_an_unsupported_declared_version()
    {
        string path = database.NewPath();
        database.Execute(path, """
            CREATE TABLE journal_metadata (
                metadata_key INTEGER PRIMARY KEY,
                schema_version INTEGER NOT NULL,
                store_epoch TEXT NOT NULL,
                updated_at_utc INTEGER NOT NULL);
            INSERT INTO journal_metadata VALUES (1, 99, '00112233445566778899aabbccddeeff', 0);
            """);

        JournalStoreException exception = Assert.Throws<JournalStoreException>(() => database.Open(path));

        Assert.Equal(JournalStoreFailureKind.SchemaMismatch, exception.Kind);
        Assert.Equal(1, database.ScalarLong(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name LIKE 'journal_%';"));
    }

    [Fact]
    public void Schema_foreign_keys_reject_orphaned_children_but_events_do_not_reference_operations()
    {
        string path = database.NewPath();
        using (database.Open(path)) { }
        using var connection = new SqliteConnection(database.ConnectionString(path));
        connection.Open();
        using SqliteCommand pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();

        using SqliteCommand orphan = connection.CreateCommand();
        orphan.CommandText = """
            INSERT INTO journal_projection(
                stream_key, section_name, source_version, projection_schema,
                projection_schema_version, data, data_sha256, updated_at_utc)
            VALUES ('missing', 'bag', 0, 'bag.v1', 1, X'01', zeroblob(32), 0);
            """;
        SqliteException exception = Assert.Throws<SqliteException>(() => orphan.ExecuteNonQuery());

        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.DoesNotContain("journal_operation", database.ForeignKeys(path, "journal_event"));
    }

    public void Dispose() => database.Dispose();
}

internal sealed class SqliteJournalTestDatabase : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ke-journal-sqlite-" + Guid.NewGuid().ToString("N"));
    private readonly List<SqliteMutationJournalStore> stores = new();

    internal SqliteJournalTestDatabase() => Directory.CreateDirectory(directory);

    internal string NewPath() => Path.Combine(directory, Guid.NewGuid().ToString("N") + ".db");
    internal string ConnectionString(string path) => new SqliteConnectionStringBuilder { DataSource = path, Pooling = true }.ToString();

    internal SqliteMutationJournalStore Open(string path, SqliteMutationJournalStoreOptions? options = null, SqliteJournalTestHook? hook = null)
    {
        var store = new SqliteMutationJournalStore(options ?? new SqliteMutationJournalStoreOptions(ConnectionString(path)), hook);
        stores.Add(store);
        return store;
    }

    internal void CreateEmpty(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
    }

    internal void Execute(string path, string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }

    internal async Task ExecuteAsync(string path, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqliteConnection(ConnectionString(path));
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    internal long ScalarLong(string path, string sql)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    internal IReadOnlyList<string> ForeignKeys(string path, string table)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list({table});";
        using SqliteDataReader reader = command.ExecuteReader();
        var targets = new List<string>();
        while (reader.Read()) targets.Add(reader.GetString(2));
        return targets;
    }

    public void Dispose()
    {
        foreach (SqliteMutationJournalStore store in stores) store.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

internal sealed class SqliteJournalManualTimeProvider : TimeProvider
{
    private DateTimeOffset utcNow;

    internal SqliteJournalManualTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;
    public override DateTimeOffset GetUtcNow() => utcNow;
    internal void Advance(TimeSpan duration) => utcNow += duration;
}
