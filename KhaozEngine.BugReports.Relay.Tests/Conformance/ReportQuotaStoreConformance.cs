using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

/// <summary>
/// The optimistic concurrency every <see cref="IReportQuotaStore"/> owes the quota gate. Self-contained so a backend test
/// project can link this file and run the same facts over its own store.
/// </summary>
public abstract class ReportQuotaStoreConformance
{
    const string Key = "gd-20261017";
    static readonly DateTimeOffset ExpiresAt = new(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Builds a fresh, empty store for one fact.</summary>
    protected abstract IReportQuotaStore CreateStore();

    [Fact]
    public async Task Read_Absent_ReturnsNull()
    {
        IReportQuotaStore store = CreateStore();

        Assert.Null(await store.ReadAsync(Key, CancellationToken.None));
    }

    [Fact]
    public async Task CreateOnly_WhenAbsent_WritesCountOne()
    {
        IReportQuotaStore store = CreateStore();

        Assert.True(await store.TryWriteAsync(Key, 1, null, ExpiresAt, CancellationToken.None));

        QuotaCounter? counter = await store.ReadAsync(Key, CancellationToken.None);
        Assert.NotNull(counter);
        Assert.Equal(1, counter.Count);
    }

    [Fact]
    public async Task CreateOnly_WhenPresent_ReturnsFalse()
    {
        IReportQuotaStore store = CreateStore();
        await store.TryWriteAsync(Key, 1, null, ExpiresAt, CancellationToken.None);

        Assert.False(await store.TryWriteAsync(Key, 1, null, ExpiresAt, CancellationToken.None));
        Assert.Equal(1, (await store.ReadAsync(Key, CancellationToken.None))!.Count);
    }

    [Fact]
    public async Task Write_WithCurrentVersion_SucceedsAndChangesVersion()
    {
        IReportQuotaStore store = CreateStore();
        await store.TryWriteAsync(Key, 1, null, ExpiresAt, CancellationToken.None);
        QuotaCounter first = (await store.ReadAsync(Key, CancellationToken.None))!;

        Assert.True(await store.TryWriteAsync(Key, 2, first.Version, ExpiresAt, CancellationToken.None));

        QuotaCounter second = (await store.ReadAsync(Key, CancellationToken.None))!;
        Assert.Equal(2, second.Count);
        Assert.NotEqual(first.Version, second.Version);
    }

    [Fact]
    public async Task Write_WithStaleVersion_ReturnsFalseAndKeepsCount()
    {
        IReportQuotaStore store = CreateStore();
        await store.TryWriteAsync(Key, 1, null, ExpiresAt, CancellationToken.None);
        QuotaCounter stale = (await store.ReadAsync(Key, CancellationToken.None))!;
        await store.TryWriteAsync(Key, 2, stale.Version, ExpiresAt, CancellationToken.None);

        Assert.False(await store.TryWriteAsync(Key, 3, stale.Version, ExpiresAt, CancellationToken.None));

        Assert.Equal(2, (await store.ReadAsync(Key, CancellationToken.None))!.Count);
    }
}
