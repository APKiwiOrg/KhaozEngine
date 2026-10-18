using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.BugReports.Relay;
using Xunit;

namespace KhaozEngine.Tests.BugReports.Relay;

/// <summary>The media store conformance suite over the in-memory store.</summary>
public sealed class InMemoryReportMediaStoreConformanceTests : ReportMediaStoreConformance
{
    protected override IReportMediaStore CreateStore() => new InMemoryReportMediaStore();
}

/// <summary>The quota store conformance suite over the in-memory store.</summary>
public sealed class InMemoryReportQuotaStoreConformanceTests : ReportQuotaStoreConformance
{
    protected override IReportQuotaStore CreateStore() => new InMemoryReportQuotaStore();
}

/// <summary>What the in-memory seams add beyond the conformance suites.</summary>
public sealed class InMemorySeamTests
{
    const string FirstId = "0192f3a4b5c67d8e9f0a1b2c3d4e5f60";
    const string SecondId = "0192f3a4b5c67d8e9f0a1b2c3d4e5f61";

    [Fact]
    public async Task InMemoryMediaStore_RecordsHeaders()
    {
        var store = new InMemoryReportMediaStore();
        var headers = new ReportBlobHeaders("image/png", "public, max-age=86400");

        await store.PutAsync(ReportArea.Public, "g/2026-10/a/screenshot.png", new byte[] { 1 }, headers, false,
            CancellationToken.None);

        Assert.Equal(headers, store.HeadersOf(ReportArea.Public, "g/2026-10/a/screenshot.png"));
        Assert.Null(store.HeadersOf(ReportArea.Pending, "g/2026-10/a/screenshot.png"));
        Assert.Equal(new[] { "g/2026-10/a/screenshot.png" }, store.Paths(ReportArea.Public));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("/x")]
    [InlineData("a//b")]
    [InlineData("A/b")]
    public async Task InMemoryMediaStore_InvalidPath_Throws(string path)
    {
        var store = new InMemoryReportMediaStore();
        var headers = new ReportBlobHeaders("application/json; charset=utf-8", "no-cache");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.PutAsync(ReportArea.Public, path, new byte[] { 1 }, headers, false, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.GetAsync(ReportArea.Public, path, 16, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ExistsAsync(ReportArea.Public, path, CancellationToken.None));
    }

    [Fact]
    public async Task InMemoryQueue_IsFifo()
    {
        var queue = new InMemoryReportWorkQueue();
        var first = new ReportWorkItem(ReportWorkKind.CreateIssue, FirstId);
        var second = new ReportWorkItem(ReportWorkKind.AppendServerSnapshot, SecondId, 1);

        await queue.EnqueueAsync(first, CancellationToken.None);
        await queue.EnqueueAsync(second, CancellationToken.None);

        Assert.Equal(2, queue.Count);
        Assert.True(queue.TryDequeue(out ReportWorkItem? a));
        Assert.True(queue.TryDequeue(out ReportWorkItem? b));
        Assert.False(queue.TryDequeue(out _));
        Assert.Equal(first, a);
        Assert.Equal(second, b);
        Assert.Equal(0, queue.Count);
    }
}
