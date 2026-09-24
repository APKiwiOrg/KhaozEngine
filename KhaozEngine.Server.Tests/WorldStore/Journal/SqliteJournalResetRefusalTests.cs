using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.Journal;
using KhaozEngine.WorldStore.Sqlite;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// What the SQLite reset refuses before it deletes anything: a bad argument, a file that is not there, a file with no
/// journal and a journal at an older schema version. None of them is created, migrated or repaired.
/// </summary>
public sealed class SqliteJournalResetRefusalTests : IDisposable
{
    private readonly SqliteJournalTestDatabase database = new();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_connection_string_is_refused(string? connectionString)
        => await Assert.ThrowsAnyAsync<ArgumentException>(() => SqliteJournalReset.ResetAsync(connectionString!));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_lock_timeout_that_is_not_positive_is_refused(int milliseconds)
    {
        ArgumentOutOfRangeException refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => SqliteJournalReset.ResetAsync(database.ConnectionString(database.NewPath()), TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Equal("lockTimeout", refused.ParamName);
    }

    [Fact]
    public async Task A_cancelled_reset_touches_nothing()
    {
        string path = database.NewPath();
        await SqliteJournalResetHarness.SeedAsync(database, path);
        var before = SqliteJournalResetHarness.Counts(database, path);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SqliteJournalReset.ResetAsync(database.ConnectionString(path), new CancellationToken(canceled: true)));

        Assert.Equal(before, SqliteJournalResetHarness.Counts(database, path));
    }

    [Fact]
    public async Task A_path_that_names_no_file_is_refused_and_no_file_is_created()
    {
        string path = database.NewPath();

        JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(
            () => SqliteJournalReset.ResetAsync(database.ConnectionString(path)));

        Assert.Equal(JournalStoreFailureKind.Unavailable, refused.Kind);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_database_with_no_journal_is_refused_and_no_journal_is_created()
    {
        string path = database.NewPath();
        database.CreateEmpty(path);

        JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(
            () => SqliteJournalReset.ResetAsync(database.ConnectionString(path)));

        Assert.Equal(JournalStoreFailureKind.SchemaMismatch, refused.Kind);
        Assert.Equal(0, database.ScalarLong(path, "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'journal%';"));
    }

    [Fact]
    public async Task A_version_one_journal_is_refused_and_left_at_version_one()
    {
        string path = database.NewPath();
        database.Execute(path, SqliteMutationJournalStore.VersionOneSchemaSqlForTest);

        JournalStoreException refused = await Assert.ThrowsAsync<JournalStoreException>(
            () => SqliteJournalReset.ResetAsync(database.ConnectionString(path)));

        Assert.Equal(JournalStoreFailureKind.SchemaMismatch, refused.Kind);
        Assert.Equal(1, database.ScalarLong(path, "SELECT schema_version FROM journal_metadata WHERE metadata_key = 1;"));
    }

    public void Dispose() => database.Dispose();
}
