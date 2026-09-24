using System;
using System.Collections.Generic;
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
        KeyValuePair<string, long>[] before = SqliteJournalResetHarness.Counts(database, path);
        string metadata = SqliteJournalResetHarness.Metadata(database, path);

        JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(
            () => SqliteJournalReset.ResetAsync(database.ConnectionString(path)));

        AssertRefusal(refused, "host table 'game_character'", "journal table 'journal_stream'", $"ON DELETE {action}");
        Assert.Equal(before, SqliteJournalResetHarness.Counts(database, path));
        Assert.Equal(metadata, SqliteJournalResetHarness.Metadata(database, path));
        Assert.Equal(
            1,
            database.ScalarLong(path, $"SELECT COUNT(*) FROM game_character WHERE stream_key = '{seeded.First}';"));
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
        KeyValuePair<string, long>[] before = SqliteJournalResetHarness.Counts(database, path);
        string metadata = SqliteJournalResetHarness.Metadata(database, path);

        JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(
            () => SqliteJournalReset.ResetAsync(database.ConnectionString(path)));

        AssertRefusal(refused, "trigger 'game_stream_audit'", "journal table 'journal_stream'", "not the journal's own");
        Assert.Equal(before, SqliteJournalResetHarness.Counts(database, path));
        Assert.Equal(metadata, SqliteJournalResetHarness.Metadata(database, path));
        Assert.Equal(0, database.ScalarLong(path, "SELECT COUNT(*) FROM game_log;"));
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
        KeyValuePair<string, long>[] before = SqliteJournalResetHarness.Counts(database, path);

        JournalResetResult reset = await SqliteJournalReset.ResetAsync(database.ConnectionString(path));

        Assert.Equal(before, JournalResetTestSupport.CountsByTable(reset));
    }

    private static void AssertRefusal(JournalStoreException refused, params string[] named)
    {
        Assert.Equal(JournalStoreFailureKind.SchemaMismatch, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
        Assert.Contains("Nothing was deleted", refused.Message, StringComparison.Ordinal);
        foreach (string name in named) Assert.Contains(name, refused.Message, StringComparison.Ordinal);
    }

    public void Dispose() => database.Dispose();
}
