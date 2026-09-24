using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Microsoft.Data.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// The reset against a journal file whose write lock another writer holds: a raw connection in one fact, and a
/// journal store parked inside its own commit in the other.
/// <para>
/// SQLite takes one writer at a time per file, and every journal writer holds that lock for its own transaction. The
/// other writer takes the lock before the reset is called and releases it only after the call has returned, so this
/// is not a race with a timing guess in it: the only outcome SQLite allows the reset is the busy refusal once its
/// lock timeout runs out. The facts pin what that refusal looks like and that nothing of the reset landed.
/// </para>
/// <para>
/// It proves nothing about HOW LONG the refusal takes. The reset is given a short lock timeout only so the fact does
/// not sit through the default.
/// </para>
/// </summary>
public sealed class SqliteJournalResetContentionTests : IDisposable
{
    private static readonly TimeSpan ShortLockTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan HookBound = TimeSpan.FromSeconds(30);
    private readonly SqliteJournalTestDatabase database = new();

    [Fact]
    public async Task A_reset_while_another_connection_holds_the_write_lock_is_refused_and_deletes_nothing()
    {
        string path = database.NewPath();
        JournalResetTestSupport.Seeded seeded = await SqliteJournalResetHarness.SeedAsync(database, path);
        KeyValuePair<string, long>[] before = SqliteJournalResetHarness.Counts(database, path);
        string metadata = SqliteJournalResetHarness.Metadata(database, path);

        // A writer that has taken the file and is holding it, which is the shape of a host still committing when a
        // release job fires. BEGIN IMMEDIATE takes the write lock at once, and the update gives the writer a change
        // of its own to commit afterwards.
        using var writer = new SqliteConnection(database.ConnectionString(path));
        writer.Open();
        Execute(writer, "BEGIN IMMEDIATE;");
        Execute(writer, $"UPDATE journal_stream SET updated_at_utc = 42 WHERE stream_key = '{seeded.Second}';");

        JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(
            () => SqliteJournalReset.ResetAsync(database.ConnectionString(path), ShortLockTimeout));

        Assert.Equal(JournalStoreFailureKind.Timeout, refused.Kind);
        Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
        Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
        Assert.Equal(5, Assert.IsType<SqliteException>(refused.InnerException).SqliteErrorCode);

        // The writer was never disturbed: its transaction is still open and still commits.
        Execute(writer, "COMMIT;");
        writer.Close();

        Assert.Equal(before, SqliteJournalResetHarness.Counts(database, path));
        Assert.Equal(metadata, SqliteJournalResetHarness.Metadata(database, path));
        Assert.Equal(
            42,
            database.ScalarLong(path, $"SELECT updated_at_utc FROM journal_stream WHERE stream_key = '{seeded.Second}';"));

        // Once the writer is gone the same call goes through and reports the journal as the writer left it.
        JournalResetResult reset = await SqliteJournalReset.ResetAsync(database.ConnectionString(path), ShortLockTimeout);
        Assert.Equal(before, JournalResetTestSupport.CountsByTable(reset));
    }

    [Fact]
    public async Task A_reset_while_a_journal_store_is_inside_its_commit_is_refused_and_counts_that_commit_afterwards()
    {
        string path = database.NewPath();
        JournalResetTestSupport.Seeded seeded = await SqliteJournalResetHarness.SeedAsync(database, path);
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var hook = new SqliteJournalTestHook(phase =>
        {
            if (phase != JournalTestHookPhase.BeforeCommit) return;
            inside.Set();
            Assert.True(release.Wait(HookBound), "The fact never released the held commit.");
        });
        using SqliteMutationJournalStore store = database.Open(path, hook: hook);

        // The store's own commit, parked after its writes and before its COMMIT, holding the lock it took for them.
        Task<JournalCommitResult> commit = Task.Run(() => store.CommitAsync(new JournalCommit(
            JournalResetTestSupport.Identity(9),
            new[] { new JournalStreamMutation(seeded.Second, 1, new[] { new JournalEvent("item.granted", 1, new byte[] { 9 }) }) },
            Array.Empty<JournalProjectionWrite>(),
            "result.v1",
            1,
            new byte[] { 9 })));
        Assert.True(inside.Wait(HookBound), "The store never reached its commit.");

        JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(
            () => SqliteJournalReset.ResetAsync(database.ConnectionString(path), ShortLockTimeout));
        Assert.Equal(JournalStoreFailureKind.Timeout, refused.Kind);
        Assert.False(commit.IsCompleted);

        release.Set();
        Assert.Equal(JournalCommitStatus.Applied, (await commit).Status);
        KeyValuePair<string, long>[] before = SqliteJournalResetHarness.Counts(database, path);

        JournalResetResult reset = await SqliteJournalReset.ResetAsync(database.ConnectionString(path), ShortLockTimeout);

        Assert.Equal(before, JournalResetTestSupport.CountsByTable(reset));
        Assert.Equal(5, reset.OperationsDeleted);
        Assert.Equal(5, reset.EventsDeleted);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose() => database.Dispose();
}
