using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using KhaozEngine.Tests.WorldStore;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.SqlServer;
using Microsoft.Data.SqlClient;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// The SQL Server reset against a game's own table that points into the journal, the SQLite facts' twin. A foreign
/// key whose delete action would fire is refused inside the reset's transaction, a trigger on a journal table that is
/// not the journal's own is refused by the schema check before it, and a key that fires nothing is left to the
/// delete itself.
/// <para>
/// Every fact drops what it created in a <c>finally</c>, and drops it first too, so a run that died half way does not
/// leave a host table behind to refuse every later reset in the shared test database.
/// </para>
/// </summary>
[Collection("SQL Server mutation journal")]
public sealed class SqlServerJournalResetHostObjectTests
{
    internal const string HostTable = "reset_test_host_character";
    private const string HostTrigger = "reset_test_stream_audit";

    [SqlServerFact]
    public Task A_host_key_declared_on_delete_cascade_is_refused_and_nothing_is_deleted()
        => AssertFiringKeyRefusedAsync("CASCADE");

    [SqlServerFact]
    public Task A_host_key_declared_on_delete_set_null_is_refused_and_nothing_is_deleted()
        => AssertFiringKeyRefusedAsync("SET NULL");

    [SqlServerFact]
    public Task A_host_key_declared_on_delete_set_default_is_refused_and_nothing_is_deleted()
        => AssertFiringKeyRefusedAsync("SET DEFAULT");

    private static async Task AssertFiringKeyRefusedAsync(string action)
    {
        await DropHostObjectsAsync();
        try
        {
            JournalResetTestSupport.Seeded seeded = await SqlServerJournalResetHarness.SeedAsync();
            await CreateHostTableAsync(action, seeded.First);
            KeyValuePair<string, long>[] before = await SqlServerJournalResetHarness.CountsAsync();
            string metadata = await SqlServerJournalResetHarness.MetadataAsync();

            JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(
                () => SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString));

            Assert.Equal(JournalStoreFailureKind.SchemaMismatch, refused.Kind);
            Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
            Assert.Equal(JournalStoreFailureScope.WholeStore, refused.Scope);
            Assert.Contains($"host table 'dbo.{HostTable}'", refused.Message, StringComparison.Ordinal);
            Assert.Contains("journal table 'journal_stream'", refused.Message, StringComparison.Ordinal);
            Assert.Contains($"ON DELETE {action}", refused.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was deleted", refused.Message, StringComparison.Ordinal);
            Assert.Equal(before, await SqlServerJournalResetHarness.CountsAsync());
            Assert.Equal(metadata, await SqlServerJournalResetHarness.MetadataAsync());
            Assert.Equal(1L, await HostRowsReferencingAsync(seeded.First));
        }
        finally
        {
            await DropHostObjectsAsync();
        }
    }

    [SqlServerFact]
    public async Task A_host_trigger_on_a_journal_table_is_refused_by_the_schema_check_and_nothing_is_deleted()
    {
        await DropHostObjectsAsync();
        try
        {
            await SqlServerJournalResetHarness.SeedAsync();
            await ExecuteAsync($"""
                CREATE TRIGGER dbo.{HostTrigger} ON dbo.journal_stream AFTER DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                END
                """);
            KeyValuePair<string, long>[] before = await SqlServerJournalResetHarness.CountsAsync();
            string metadata = await SqlServerJournalResetHarness.MetadataAsync();

            JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(
                () => SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString));

            Assert.Equal(JournalStoreFailureKind.SchemaMismatch, refused.Kind);
            Assert.Equal(JournalStoreFailureCertainty.DefinitelyNotCommitted, refused.Certainty);
            Assert.Contains("triggers do not match", refused.Message, StringComparison.Ordinal);
            Assert.Equal(before, await SqlServerJournalResetHarness.CountsAsync());
            Assert.Equal(metadata, await SqlServerJournalResetHarness.MetadataAsync());
        }
        finally
        {
            await DropHostObjectsAsync();
        }
    }

    [SqlServerFact]
    public async Task A_host_key_that_fires_nothing_and_holds_no_row_lets_the_reset_through()
    {
        await DropHostObjectsAsync();
        try
        {
            await SqlServerJournalResetHarness.SeedAsync();
            await CreateHostTableAsync("NO ACTION", streamKey: null);
            KeyValuePair<string, long>[] before = await SqlServerJournalResetHarness.CountsAsync();

            JournalResetResult reset = await SqlServerJournalReset.ResetAsync(SqlServerJournalResetHarness.ConnectionString);

            Assert.Equal(before, JournalResetTestSupport.CountsByTable(reset));
        }
        finally
        {
            await DropHostObjectsAsync();
        }
    }

    /// <summary>
    /// The host table, keyed into <c>dbo.journal_stream</c> with <paramref name="action"/>, holding one row that
    /// references <paramref name="streamKey"/> when one is given. The column matches the stream key's type and
    /// collation, which SQL Server requires of a foreign key.
    /// </summary>
    internal static async Task CreateHostTableAsync(string action, string? streamKey)
    {
        await ExecuteAsync($"""
            CREATE TABLE dbo.{HostTable} (
                character_id int NOT NULL PRIMARY KEY,
                stream_key nvarchar(256) COLLATE Latin1_General_100_BIN2 NULL
                    CONSTRAINT fk_{HostTable}_stream REFERENCES dbo.journal_stream(stream_key) ON DELETE {action});
            """);
        if (streamKey is not null)
            await ExecuteAsync($"INSERT INTO dbo.{HostTable} (character_id, stream_key) VALUES (1, @stream);", streamKey);
    }

    /// <summary>The host rows that still reference <paramref name="streamKey"/>.</summary>
    internal static async Task<long> HostRowsReferencingAsync(string streamKey)
    {
        await using var connection = new SqlConnection(SqlServerJournalResetHarness.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM dbo.{HostTable} WHERE stream_key = @stream;";
        command.Parameters.Add("@stream", SqlDbType.NVarChar, 256).Value = streamKey;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>Drops the host table and the host trigger if either stands. Safe to run when neither does.</summary>
    internal static async Task DropHostObjectsAsync()
    {
        await ExecuteAsync($"DROP TRIGGER IF EXISTS dbo.{HostTrigger};");
        await ExecuteAsync($"DROP TABLE IF EXISTS dbo.{HostTable};");
    }

    private static async Task ExecuteAsync(string sql, string? stream = null)
    {
        await using var connection = new SqlConnection(SqlServerJournalResetHarness.ConnectionString);
        await connection.OpenAsync();
        await using SqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        if (stream is not null) command.Parameters.Add("@stream", SqlDbType.NVarChar, 256).Value = stream;
        await command.ExecuteNonQueryAsync();
    }
}
