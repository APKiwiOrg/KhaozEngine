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
/// A SQL Server reset that fails PART WAY through its deletes rolls every one of them back, the SQLite fact's twin.
/// <para>
/// A game table holds a plain <c>NO ACTION</c> key into <c>dbo.journal_stream</c> and a row behind it. The key fires
/// nothing, so the reset lets it through, and the reset deletes children before parents, so the five tables below
/// <c>journal_stream</c> are already empty inside the transaction when the last delete breaks the key. The host table
/// is dropped in a <c>finally</c>, and before the fact too, so the shared test database never keeps it.
/// </para>
/// </summary>
[Collection("SQL Server mutation journal")]
public sealed class SqlServerJournalResetAtomicityTests
{
    [SqlServerFact]
    public async Task A_host_row_that_breaks_the_last_delete_rolls_every_table_back()
    {
        await SqlServerJournalResetHostObjectTests.DropHostObjectsAsync();
        try
        {
            JournalResetTestSupport.Seeded seeded = await SqlServerJournalResetHarness.SeedAsync();
            await SqlServerJournalResetHostObjectTests.CreateHostTableAsync("NO ACTION", seeded.First);
            KeyValuePair<string, long>[] before = await SqlServerJournalResetHarness.CountsAsync();
            string metadata = await SqlServerJournalResetHarness.MetadataAsync();
            Assert.All(before, table => Assert.True(table.Value > 0, $"The seed left {table.Key} empty."));

            JournalStoreException failed = await Assert.ThrowsAsync<JournalStoreException>(
                () => SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString));

            Assert.Equal(JournalStoreFailureKind.ConstraintViolation, failed.Kind);
            Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, failed.Certainty);
            Assert.Equal(JournalStoreFailureScope.WholeStore, failed.Scope);
            SqlException provider = Assert.IsType<SqlException>(failed.InnerException);
            Assert.Equal(547, provider.Number);
            Assert.Contains(SqlServerJournalResetHostObjectTests.HostTable, provider.Message, StringComparison.Ordinal);
            Assert.Equal(before, await SqlServerJournalResetHarness.CountsAsync());
            Assert.Equal(metadata, await SqlServerJournalResetHarness.MetadataAsync());
            Assert.Equal(1L, await SqlServerJournalResetHostObjectTests.HostRowsReferencingAsync(seeded.First));
        }
        finally
        {
            await SqlServerJournalResetHostObjectTests.DropHostObjectsAsync();
        }
    }
}
