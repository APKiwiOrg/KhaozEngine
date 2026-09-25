using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// The SQL Server journal reset, the SQLite facts' twin: what it deletes, what it keeps, what a second reset finds,
/// that the delete guard is shut again afterwards, and what the emptied journal answers. Gated by
/// <c>KE_SQLSERVER_TEST_CONNSTRING</c> like every other SQL Server journal fact, and run only against a database named
/// for journal tests. Read <see cref="SqlServerJournalResetHarness"/> for why a whole-journal wipe is safe there.
/// </summary>
[Collection("SQL Server mutation journal")]
public sealed class SqlServerJournalResetTests
{
    [SqlServerFact]
    public async Task A_reset_empties_every_data_table_and_reports_what_each_held()
    {
        await SqlServerJournalResetHarness.SeedAsync();
        KeyValuePair<string, long>[] before = await SqlServerJournalResetHarness.CountsAsync();
        Assert.All(before, table => Assert.True(table.Value > 0, $"The seed left {table.Key} empty."));

        JournalResetResult result = await SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString);

        // Keyed by the tables the database holds, so a data table the reset does not report is a failure here.
        Assert.Equal(before, JournalResetTestSupport.CountsByTable(result));
        Assert.All(
            await SqlServerJournalResetHarness.CountsAsync(),
            table => Assert.True(table.Value == 0, $"{table.Key} still holds {table.Value} rows after the reset."));
    }

    [SqlServerFact]
    public async Task A_reset_leaves_the_metadata_row_and_its_epoch_exactly_as_they_were()
    {
        await SqlServerJournalResetHarness.SeedAsync();
        string metadata = await SqlServerJournalResetHarness.MetadataAsync();
        Guid epoch = await SqlServerJournalResetHarness.EpochAsync();

        JournalResetResult result = await SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString);

        Assert.Equal(epoch, result.StoreEpoch);
        Assert.Equal(metadata, await SqlServerJournalResetHarness.MetadataAsync());
    }

    [SqlServerFact]
    public async Task A_second_reset_finds_nothing_and_reports_zero_for_every_table()
    {
        await SqlServerJournalResetHarness.SeedAsync();
        JournalResetResult first = await SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString);
        string metadata = await SqlServerJournalResetHarness.MetadataAsync();

        JournalResetResult second = await SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString);

        Assert.True(first.RowsDeleted > 0);
        Assert.Equal(new JournalResetResult(first.StoreEpoch, 0, 0, 0, 0, 0, 0), second);
        Assert.Equal(metadata, await SqlServerJournalResetHarness.MetadataAsync());
    }

    [SqlServerFact]
    public async Task After_a_reset_the_delete_guard_is_shut_and_a_plain_delete_is_refused()
    {
        await SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString);
        JournalResetTestSupport.Seeded seeded = await SqlServerJournalResetHarness.SeedAsync();

        await using var connection = new SqlConnection(SqlServerJournalResetHarness.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM dbo.journal_operation_stream; DELETE FROM dbo.journal_operation;";
        await using (SqlTransaction transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            delete.Transaction = transaction;
            SqlException refused = await Assert.ThrowsAsync<SqlException>(() => delete.ExecuteNonQueryAsync());
            Assert.Equal(51000, refused.Number);
        }

        // The whole batch rolled back with the trigger's refusal, so every receipt is still there.
        Assert.Contains(
            new KeyValuePair<string, long>("journal_operation", seeded.Operations.Count),
            await SqlServerJournalResetHarness.CountsAsync());
        await SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString);
    }

    [SqlServerFact]
    public async Task A_reset_journal_forgets_every_receipt_and_takes_the_same_keys_again()
    {
        JournalResetTestSupport.Seeded seeded = await SqlServerJournalResetHarness.SeedAsync();

        await SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString);

        SqlServerMutationJournalStore store = SqlServerJournalResetHarness.OpenStore();
        foreach (JournalOperationIdentity operation in seeded.Operations)
            Assert.Equal(JournalOperationResolutionStatus.NotFound, (await store.ResolveOperationAsync(operation)).Status);
        Assert.Null(await store.LoadSnapshotAsync(seeded.First));
        JournalInitializeResult again = await store.InitializeAsync(
            JournalResetTestSupport.Initialization(JournalResetTestSupport.Identity(9), seeded.First, 9));
        Assert.Equal(JournalInitializeStatus.Initialized, again.Status);
        await SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString);
    }
}
