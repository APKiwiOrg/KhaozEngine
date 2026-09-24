using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// The SQLite journal reset against a journal with rows in every data table: what it deletes, what it keeps, what a
/// second reset finds, and what the emptied journal answers afterwards.
/// </summary>
public sealed class SqliteJournalResetTests : IDisposable
{
    private readonly SqliteJournalTestDatabase database = new();

    [Fact]
    public async Task A_reset_empties_every_data_table_and_reports_what_each_held()
    {
        string path = database.NewPath();
        await SqliteJournalResetHarness.SeedAsync(database, path);
        KeyValuePair<string, long>[] before = SqliteJournalResetHarness.Counts(database, path);
        Assert.All(before, table => Assert.True(table.Value > 0, $"The seed left {table.Key} empty."));

        JournalResetResult result = await SqliteJournalReset.ResetAsync(database.ConnectionString(path));

        // Keyed by the tables the file holds, so a data table the reset does not report is a failure here.
        Assert.Equal(before, JournalResetTestSupport.CountsByTable(result));
        Assert.All(
            SqliteJournalResetHarness.Counts(database, path),
            table => Assert.True(table.Value == 0, $"{table.Key} still holds {table.Value} rows after the reset."));
    }

    [Fact]
    public async Task A_reset_leaves_the_metadata_row_and_its_epoch_exactly_as_they_were()
    {
        string path = database.NewPath();
        await SqliteJournalResetHarness.SeedAsync(database, path);
        string metadata = SqliteJournalResetHarness.Metadata(database, path);
        Guid epoch = SqliteJournalResetHarness.Epoch(database, path);

        JournalResetResult result = await SqliteJournalReset.ResetAsync(database.ConnectionString(path));

        Assert.Equal(epoch, result.StoreEpoch);
        Assert.Equal(metadata, SqliteJournalResetHarness.Metadata(database, path));
    }

    [Fact]
    public async Task A_second_reset_finds_nothing_and_reports_zero_for_every_table()
    {
        string path = database.NewPath();
        await SqliteJournalResetHarness.SeedAsync(database, path);
        JournalResetResult first = await SqliteJournalReset.ResetAsync(database.ConnectionString(path));
        string metadata = SqliteJournalResetHarness.Metadata(database, path);

        JournalResetResult second = await SqliteJournalReset.ResetAsync(database.ConnectionString(path));

        Assert.True(first.RowsDeleted > 0);
        Assert.Equal(new JournalResetResult(first.StoreEpoch, 0, 0, 0, 0, 0, 0), second);
        Assert.Equal(metadata, SqliteJournalResetHarness.Metadata(database, path));
    }

    [Fact]
    public async Task A_reset_journal_forgets_every_receipt_and_takes_the_same_keys_again()
    {
        string path = database.NewPath();
        JournalResetTestSupport.Seeded seeded = await SqliteJournalResetHarness.SeedAsync(database, path);

        await SqliteJournalReset.ResetAsync(database.ConnectionString(path));

        using SqliteMutationJournalStore store = database.Open(
            path,
            new SqliteMutationJournalStoreOptions(database.ConnectionString(path))
            {
                SchemaMode = SqliteJournalSchemaMode.ValidateOnly,
            });
        foreach (JournalOperationIdentity operation in seeded.Operations)
            Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(operation)).Status);
        Assert.Null(await store.LoadSnapshotAsync(seeded.First));
        JournalInitializeResult again = await store.InitializeAsync(
            JournalResetTestSupport.Initialization(JournalResetTestSupport.Identity(9), seeded.First, 9));
        Assert.Equal(JournalInitializeStatus.Initialized, again.Status);
    }

    [Fact]
    public async Task A_cursor_from_before_the_reset_finds_no_stream_then_one_it_cannot_trust()
    {
        string path = database.NewPath();
        JournalResetTestSupport.Seeded seeded = await SqliteJournalResetHarness.SeedAsync(database, path);
        string cursor;
        using (SqliteMutationJournalStore before = database.Open(path))
        {
            JournalProjectionRead read = await before.ReadProjectionsAsync(new JournalProjectionQuery(seeded.First));
            Assert.Equal(3, read.HeadVersion);
            cursor = read.Cursor!;
        }

        await SqliteJournalReset.ResetAsync(database.ConnectionString(path));

        using SqliteMutationJournalStore store = database.Open(path);
        Assert.Equal(
            JournalProjectionReadStatus.NotFound,
            (await store.ReadProjectionsAsync(new JournalProjectionQuery(seeded.First, cursor))).Status);
        await store.InitializeAsync(JournalResetTestSupport.Initialization(JournalResetTestSupport.Identity(9), seeded.First, 9));

        // The epoch is kept, so only the captured head, three against a new head of zero, tells this cursor apart.
        JournalProjectionRead after = await store.ReadProjectionsAsync(new JournalProjectionQuery(seeded.First, cursor));
        Assert.Equal(JournalProjectionReadStatus.ResetRequired, after.Status);
        Assert.Equal(
            JournalProjectionCursor.DecodeForTest(cursor).Epoch,
            JournalProjectionCursor.DecodeForTest(after.Cursor!).Epoch);
    }

    public void Dispose() => database.Dispose();
}
