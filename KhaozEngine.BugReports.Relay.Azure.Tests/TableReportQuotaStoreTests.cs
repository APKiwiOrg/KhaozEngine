using System;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Runs the shared quota contract over the Azure store and its Table seam.</summary>
public sealed class TableReportQuotaStoreConformanceTests : ReportQuotaStoreConformance
{
    protected override IReportQuotaStore CreateStore() => new TableReportQuotaStore(new InMemoryQuotaTable());
}

/// <summary>The quota store chooses conditional add or replace and keeps the caller's expiry.</summary>
public sealed class TableReportQuotaStoreTests
{
    [Fact]
    public async Task Write_CreateOnly_UsesAdd_VersionUsesReplace()
    {
        var table = new InMemoryQuotaTable();
        var store = new TableReportQuotaStore(table);
        var expires = new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero);
        await store.TryWriteAsync("key", 1, null, expires, default);
        QuotaCounter counter = (await store.ReadAsync("key", default))!;
        Assert.True(await store.TryWriteAsync("key", 2, counter.Version, expires, default));
        Assert.Equal(1, table.Adds);
        Assert.Equal(1, table.Replaces);
        Assert.Equal(expires, table.LastExpiry);
        Assert.Equal(2, (await store.ReadAsync("key", default))!.Count);
    }
}
