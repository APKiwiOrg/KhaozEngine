using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// A SQLite reset that fails PART WAY through its deletes rolls every one of them back.
/// <para>
/// The refusals elsewhere all land before the first delete, so none of them proves the transaction. Here a game table
/// holds a plain <c>NO ACTION</c> key into <c>journal_stream</c> and a row behind it. The key fires nothing, so the
/// reset lets it through, and the reset deletes children before parents, so the five tables below
/// <c>journal_stream</c> are already empty inside the transaction when the last delete breaks the key. Every count
/// and the whole metadata row reading as they stood afterwards is the rollback.
/// </para>
/// </summary>
public sealed class SqliteJournalResetAtomicityTests : IDisposable
{
    private readonly SqliteJournalTestDatabase database = new();

    [Fact]
    public async Task A_host_row_that_breaks_the_last_delete_rolls_every_table_back()
    {
        string path = database.NewPath();
        JournalResetTestSupport.Seeded seeded = await SqliteJournalResetHarness.SeedAsync(database, path);
        database.Execute(
            path,
            """
            CREATE TABLE game_character (
                character_id INTEGER NOT NULL PRIMARY KEY,
                stream_key TEXT REFERENCES journal_stream(stream_key));
            INSERT INTO game_character (character_id, stream_key) VALUES (1, $stream);
            """,
            ("$stream", seeded.First));
        KeyValuePair<string, long>[] before = SqliteJournalResetHarness.Counts(database, path);
        string metadata = SqliteJournalResetHarness.Metadata(database, path);
        Assert.All(before, table => Assert.True(table.Value > 0, $"The seed left {table.Key} empty."));

        JournalStoreException failed = await Assert.ThrowsAsync<JournalStoreException>(
            () => SqliteJournalReset.ResetAsync(database.ConnectionString(path)));

        Assert.Equal(JournalStoreFailureKind.ConstraintViolation, failed.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, failed.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, failed.Scope);
        SqliteException provider = Assert.IsType<SqliteException>(failed.InnerException);
        Assert.Equal(19, provider.SqliteErrorCode);
        Assert.Contains("FOREIGN KEY", provider.Message, StringComparison.Ordinal);
        Assert.Equal(before, SqliteJournalResetHarness.Counts(database, path));
        Assert.Equal(metadata, SqliteJournalResetHarness.Metadata(database, path));
        Assert.Equal(
            1,
            database.ScalarLong(path, $"SELECT COUNT(*) FROM game_character WHERE stream_key = '{seeded.First}';"));

        // With the game row gone the same call goes through and reports the journal as it stood.
        database.Execute(path, "DELETE FROM game_character;");
        JournalResetResult reset = await SqliteJournalReset.ResetAsync(database.ConnectionString(path));
        Assert.Equal(before, JournalResetTestSupport.CountsByTable(reset));
    }

    public void Dispose() => database.Dispose();
}
