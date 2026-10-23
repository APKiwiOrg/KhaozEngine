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
    public async Task MediaStoreWithoutListOrDelete_ThrowsNotSupportedNamingTheMember()
    {
        IReportMediaStore store = new MediaStoreWithoutListOrDelete();

        NotSupportedException list = await Assert.ThrowsAsync<NotSupportedException>(() =>
            store.ListAsync(ReportArea.Public, "g/2026-10/a/", CancellationToken.None));
        NotSupportedException delete = await Assert.ThrowsAsync<NotSupportedException>(() =>
            store.DeleteAsync(ReportArea.Public, "g/2026-10/a/x.json", CancellationToken.None));

        Assert.Contains("ListAsync", list.Message);
        Assert.Contains("DeleteAsync", delete.Message);
    }

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
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(ReportArea.Public, path, CancellationToken.None));
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

    [Fact]
    public async Task InMemoryQueue_DelayedItem_IsHiddenUntilItsTime()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
        var queue = new InMemoryReportWorkQueue(clock);
        var first = new ReportWorkItem(ReportWorkKind.PurgeRevoked, FirstId);
        var second = new ReportWorkItem(ReportWorkKind.CreateIssue, SecondId);

        await queue.EnqueueAsync(first, TimeSpan.FromMinutes(12), CancellationToken.None);
        await queue.EnqueueAsync(second, CancellationToken.None);

        Assert.Equal(2, queue.Count);
        Assert.True(queue.TryDequeue(out ReportWorkItem? immediate));
        Assert.Equal(second, immediate);
        Assert.False(queue.TryDequeue(out ReportWorkItem? hidden));
        Assert.Null(hidden);
        Assert.Equal(1, queue.Count);
        clock.Advance(TimeSpan.FromMinutes(12) - TimeSpan.FromTicks(1));
        Assert.False(queue.TryDequeue(out _));
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(queue.TryDequeue(out ReportWorkItem? delayed));
        Assert.Equal(first, delayed);
        Assert.Equal(0, queue.Count);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(6048000000001L)]
    public async Task InMemoryQueue_RejectsNegativeOrOverSevenDayDelay(long ticks)
    {
        var queue = new InMemoryReportWorkQueue();
        var item = new ReportWorkItem(ReportWorkKind.CreateIssue, FirstId);

        ArgumentOutOfRangeException error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            queue.EnqueueAsync(item, TimeSpan.FromTicks(ticks), CancellationToken.None));

        Assert.Equal("visibleAfter", error.ParamName);
        Assert.Equal(0, queue.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task InMemoryQueue_AcceptsZeroAndSevenDayDelays(int days)
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
        var queue = new InMemoryReportWorkQueue(clock);
        var item = new ReportWorkItem(ReportWorkKind.CreateIssue, FirstId);

        await queue.EnqueueAsync(item, TimeSpan.FromDays(days), CancellationToken.None);
        if (days > 0)
            Assert.False(queue.TryDequeue(out _));
        clock.Advance(TimeSpan.FromDays(days));

        Assert.True(queue.TryDequeue(out ReportWorkItem? visible));
        Assert.Equal(item, visible);
    }

    [Fact]
    public async Task InMemoryQueue_TakesOldestEnqueuedVisibleItem()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero));
        var queue = new InMemoryReportWorkQueue(clock);
        var first = new ReportWorkItem(ReportWorkKind.CreateIssue, FirstId);
        var second = new ReportWorkItem(ReportWorkKind.CreateIssue, SecondId);
        await queue.EnqueueAsync(first, TimeSpan.FromMinutes(12), CancellationToken.None);
        await queue.EnqueueAsync(second, TimeSpan.FromMinutes(6), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(12));

        Assert.True(queue.TryDequeue(out ReportWorkItem? a));
        Assert.True(queue.TryDequeue(out ReportWorkItem? b));
        Assert.Equal(first, a);
        Assert.Equal(second, b);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public async Task QueueWithoutDelay_ThrowsNotSupportedNamingTheMember()
    {
        IReportWorkQueue queue = new QueueWithoutDelay();
        var item = new ReportWorkItem(ReportWorkKind.CreateIssue, FirstId);

        NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(() =>
            queue.EnqueueAsync(item, TimeSpan.FromMinutes(12), CancellationToken.None));

        Assert.Contains("IReportWorkQueue.EnqueueAsync", error.Message);
    }

    sealed class QueueWithoutDelay : IReportWorkQueue
    {
        public Task EnqueueAsync(ReportWorkItem item, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    sealed class MediaStoreWithoutListOrDelete : IReportMediaStore
    {
        public Task<bool> PutAsync(ReportArea area, string path, ReadOnlyMemory<byte> content, ReportBlobHeaders headers,
            bool overwrite, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<ReadOnlyMemory<byte>?> GetAsync(ReportArea area, string path, int maxBytes, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<bool> ExistsAsync(ReportArea area, string path, CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }
}
