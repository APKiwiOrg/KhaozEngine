using System;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// The SQLite reset against a game's own table that points into the journal. A foreign key whose delete action would
/// fire, or a trigger on a journal table that is not the journal's own, is refused before anything is deleted. A key
/// that fires nothing is left to the delete itself.
/// <para>
/// The game table lives in the fact's own database file, which is deleted with the file on dispose whether the fact
/// passes or not.
/// </para>
/// </summary>
public sealed class SqliteJournalResetHostObjectTests : IDisposable
{
    private readonly SqliteJournalTestDatabase database = new();

    [Theory]
    [InlineData("CASCADE")]
    [InlineData("SET NULL")]
    [InlineData("SET DEFAULT")]
    public async Task A_host_key_whose_delete_action_would_fire_is_refused_and_nothing_is_deleted(string action)
    {
        string path = database.NewPath();
        JournalResetTestSupport.Seeded seeded = await SqliteJournalResetHarness.SeedAsync(database, path);
        database.Execute(
            path,
            $"""
            CREATE TABLE game_character (
                character_id INTEGER NOT NULL PRIMARY KEY,
                stream_key TEXT REFERENCES journal_stream(stream_key) ON DELETE {action});
            INSERT INTO game_character (character_id, stream_key) VALUES (1, $stream);
            """,
            ("$stream", seeded.First));
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);

        JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(
            () => SqliteJournalReset.ResetAsync(database.ConnectionString(path)));

        AssertRefusal(refused, JournalStoreRefusal.HostForeignKeyActionUnsafe,
            new JournalHostObjectFinding(JournalStoreRefusal.HostForeignKeyActionUnsafe, "game_character", "journal_stream", action, null));
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        Assert.Equal(
            1,
            database.ScalarLong(path, $"SELECT COUNT(*) FROM game_character WHERE stream_key = '{seeded.First}';"));
        database.Execute(path, "DROP TABLE game_character;");
        await SqliteJournalReset.ResetAsync(database.ConnectionString(path));
        var counts = SqliteJournalResetHarness.Counts(database, path);
        Assert.Equal(6, counts.Length);
        Assert.All(counts, table => Assert.Equal(0, table.Value));
    }

    [Fact]
    public async Task A_host_trigger_on_a_journal_table_is_refused_and_nothing_is_deleted()
    {
        string path = database.NewPath();
        await SqliteJournalResetHarness.SeedAsync(database, path);
        database.Execute(
            path,
            """
            CREATE TABLE game_log (entry TEXT NOT NULL);
            CREATE TRIGGER game_stream_audit AFTER DELETE ON journal_stream
            BEGIN
                INSERT INTO game_log (entry) VALUES (OLD.stream_key);
            END;
            """);
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);

        JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(
            () => SqliteJournalReset.ResetAsync(database.ConnectionString(path)));

        AssertRefusal(refused, JournalStoreRefusal.HostTriggerUnsafe,
            new JournalHostObjectFinding(JournalStoreRefusal.HostTriggerUnsafe, null, "journal_stream", null, "game_stream_audit"));
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        Assert.Equal(0, database.ScalarLong(path, "SELECT COUNT(*) FROM game_log;"));
        database.Execute(path, "DROP TRIGGER game_stream_audit;");
        await SqliteJournalReset.ResetAsync(database.ConnectionString(path));
        var counts = SqliteJournalResetHarness.Counts(database, path);
        Assert.Equal(6, counts.Length);
        Assert.All(counts, table => Assert.Equal(0, table.Value));
    }

    [Theory]
    [InlineData("NO ACTION")]
    [InlineData("RESTRICT")]
    public async Task A_host_key_that_fires_nothing_and_holds_no_row_lets_the_reset_through(string action)
    {
        string path = database.NewPath();
        await SqliteJournalResetHarness.SeedAsync(database, path);
        database.Execute(
            path,
            $"""
            CREATE TABLE game_character (
                character_id INTEGER NOT NULL PRIMARY KEY,
                stream_key TEXT REFERENCES journal_stream(stream_key) ON DELETE {action});
            """);
        var before = SqliteJournalResetHarness.Counts(database, path);

        JournalResetResult reset = await SqliteJournalReset.ResetAsync(database.ConnectionString(path));

        Assert.Equal(before, JournalResetTestSupport.CountsByTable(reset));
    }

    [Fact]
    public async Task Host_findings_keep_all_keys_before_all_triggers_in_provider_order()
    {
        string path = database.NewPath();
        var seeded = await SqliteJournalResetHarness.SeedAsync(database, path);
        database.Execute(path, """
            CREATE TABLE game_z(stream_key TEXT REFERENCES journal_stream(stream_key) ON DELETE SET NULL);
            CREATE TABLE game_a(stream_key TEXT REFERENCES journal_stream(stream_key) ON DELETE CASCADE);
            INSERT INTO game_z VALUES ($stream);
            INSERT INTO game_a VALUES ($stream);
            CREATE TABLE game_log(value TEXT);
            CREATE TRIGGER game_z_trigger AFTER DELETE ON journal_stream BEGIN INSERT INTO game_log VALUES (OLD.stream_key); END;
            CREATE TRIGGER game_a_trigger AFTER DELETE ON journal_event BEGIN INSERT INTO game_log VALUES (OLD.stream_key); END;
            """, ("$stream", seeded.First));
        SqliteJournalFileState before = SqliteJournalFileState.Capture(path);
        var refused = await Assert.ThrowsAsync<JournalStoreException>(() => SqliteJournalReset.ResetAsync(database.ConnectionString(path)));
        AssertRefusal(refused, JournalStoreRefusal.HostForeignKeyActionUnsafe,
            new JournalHostObjectFinding(JournalStoreRefusal.HostForeignKeyActionUnsafe, "game_a", "journal_stream", "CASCADE", null),
            new JournalHostObjectFinding(JournalStoreRefusal.HostForeignKeyActionUnsafe, "game_z", "journal_stream", "SET NULL", null),
            new JournalHostObjectFinding(JournalStoreRefusal.HostTriggerUnsafe, null, "journal_event", null, "game_a_trigger"),
            new JournalHostObjectFinding(JournalStoreRefusal.HostTriggerUnsafe, null, "journal_stream", null, "game_z_trigger"));
        Assert.Equal(before, SqliteJournalFileState.Capture(path));
        database.Execute(path, "DROP TABLE game_a; DROP TABLE game_z; DROP TRIGGER game_a_trigger; DROP TRIGGER game_z_trigger;");
        await SqliteJournalReset.ResetAsync(database.ConnectionString(path));
        var counts = SqliteJournalResetHarness.Counts(database, path);
        Assert.Equal(6, counts.Length);
        Assert.All(counts, table => Assert.Equal(0, table.Value));
    }

    private static void AssertRefusal(JournalStoreException refused, JournalStoreRefusal reason, params JournalHostObjectFinding[] named)
    {
        Assert.Equal(JournalStoreFailureKind.SchemaMismatch, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
        Assert.Equal(reason, refused.Reason);
        Assert.Equal(named, refused.HostObjects);
        Assert.Empty(refused.StreamKeys);
    }

    public void Dispose() => database.Dispose();
}
