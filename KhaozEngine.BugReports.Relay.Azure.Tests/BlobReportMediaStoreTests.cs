using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using KhaozEngine.BugReports.Relay.Azure;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay.Azure;

/// <summary>Runs the shared storage contract over the Azure store and its container seams.</summary>
public sealed class BlobReportMediaStoreConformanceTests : ReportMediaStoreConformance
{
    protected override IReportMediaStore CreateStore()
        => new BlobReportMediaStore(new InMemoryReportBlobContainer(), new InMemoryReportBlobContainer());
}

/// <summary>Container selection, upload policy and path validation at the backend boundary.</summary>
public sealed class BlobReportMediaStoreTests
{
    static readonly ReportBlobHeaders Headers = new("application/json; charset=utf-8", "no-cache");

    [Fact]
    public async Task Put_CreateOnly_PassesOverwriteFalse()
    {
        var container = new InMemoryReportBlobContainer();
        var store = new BlobReportMediaStore(container, new InMemoryReportBlobContainer());
        Assert.True(await store.PutAsync(ReportArea.Public, "a/report.json", new byte[] { 1 }, Headers, false, default));
        Assert.Equal(false, container.LastOverwrite);
    }

    [Fact]
    public async Task Put_PassesContentTypeAndCacheControl()
    {
        var container = new InMemoryReportBlobContainer();
        var store = new BlobReportMediaStore(container, new InMemoryReportBlobContainer());
        await store.PutAsync(ReportArea.Public, "a/report.json", new byte[] { 1 }, Headers, true, default);
        Assert.Equal(Headers, container.LastHeaders);
    }

    [Fact]
    public async Task Areas_MapToTheirContainers()
    {
        var publicContainer = new InMemoryReportBlobContainer();
        var pending = new InMemoryReportBlobContainer();
        var store = new BlobReportMediaStore(publicContainer, pending);
        await store.PutAsync(ReportArea.Public, "public.json", new byte[] { 1 }, Headers, false, default);
        await store.PutAsync(ReportArea.Pending, "private.json", new byte[] { 2 }, Headers, false, default);
        Assert.True(await publicContainer.ExistsAsync("public.json", default));
        Assert.False(await pending.ExistsAsync("public.json", default));
        Assert.True(await pending.ExistsAsync("private.json", default));
        Assert.False(await publicContainer.ExistsAsync("private.json", default));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../report.json")]
    [InlineData("/report.json")]
    [InlineData("UPPER.json")]
    [InlineData("a//report.json")]
    [InlineData("a\\report.json")]
    public async Task InvalidPaths_AreRefused(string path)
    {
        var store = new BlobReportMediaStore(new InMemoryReportBlobContainer(), new InMemoryReportBlobContainer());
        await Assert.ThrowsAsync<ArgumentException>(() => store.PutAsync(ReportArea.Public, path, new byte[1], Headers, false, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(ReportArea.Public, path, 10, default));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ExistsAsync(ReportArea.Public, path, default));
    }

    [Fact]
    public async Task InvalidAreaAndNegativeCap_AreRefused()
    {
        var store = new BlobReportMediaStore(new InMemoryReportBlobContainer(), new InMemoryReportBlobContainer());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ExistsAsync((ReportArea)99, "a.json", default));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetAsync(ReportArea.Public, "a.json", -1, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetAsync(ReportArea.Public, "a.json", 1, new CancellationToken(true)));
    }
}
