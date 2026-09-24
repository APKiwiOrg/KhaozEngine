using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldStore.SqlServer;
using Xunit;

namespace KhaozEngine.Tests.WorldStore.Journal;

/// <summary>
/// What the SQL Server reset refuses before it opens a connection, so these run without a server. The connection
/// string names a server that does not exist, which proves nothing was opened.
/// </summary>
public sealed class SqlServerJournalResetArgumentTests
{
    private const string Unreachable = "Server=tcp:unreachable.invalid,1;Initial Catalog=never-journal-test-db;Connect Timeout=1";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_missing_connection_string_is_refused(string? connectionString)
        => await Assert.ThrowsAnyAsync<ArgumentException>(() => SqlServerJournalReset.ResetAsync(connectionString!));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task A_lock_timeout_that_is_not_positive_is_refused(int milliseconds)
    {
        ArgumentOutOfRangeException refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => SqlServerJournalReset.ResetAsync(Unreachable, TimeSpan.FromMilliseconds(milliseconds)));

        Assert.Equal("lockTimeout", refused.ParamName);
    }

    [Fact]
    public async Task A_lock_timeout_past_the_millisecond_range_is_refused()
    {
        ArgumentOutOfRangeException refused = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => SqlServerJournalReset.ResetAsync(Unreachable, TimeSpan.FromMilliseconds(int.MaxValue + 1.0)));

        Assert.Equal("lockTimeout", refused.ParamName);
    }

    [Fact]
    public async Task A_cancelled_reset_is_refused_before_it_opens_anything()
        => await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SqlServerJournalReset.ResetAsync(Unreachable, new CancellationToken(canceled: true)));
}
