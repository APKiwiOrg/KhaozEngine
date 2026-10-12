using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Proves bounded request-order retention without disturbing pending or concurrent builds.</summary>
public sealed class ChunkRetentionTests
{
    static readonly WorldMapStyle Style = new() { PixelsPerMetre = 0.125f };
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>After six ordered builds only the four most recently requested images remain.</summary>
    [Fact]
    public void Retention_EvictsOldestRequested_BeyondCap()
    {
        using var cache = Cache(4);
        Assert.Equal(4, cache.MaxRetainedChunks);
        for (int x = 0; x < 6; x++) Build(cache, new(x, 0));
        Assert.Equal(4, cache.RetainedCount);
        for (int x = 0; x < 6; x++)
        {
            Assert.Equal(x >= 2, cache.TryGet(new(x, 0), out _));
            Assert.Equal(x >= 2, cache.IsCurrent(new(x, 0)));
        }
    }

    /// <summary>A request for a current image refreshes its retention age without rebuilding it.</summary>
    [Fact]
    public void Retention_CurrentRequestRefreshesAge()
    {
        using var cache = Cache(2);
        Build(cache, new(0, 0));
        Build(cache, new(1, 0));
        cache.Request(new(0, 0), 0f);
        Assert.False(cache.BuildNext());
        Build(cache, new(2, 0));
        Assert.True(cache.IsCurrent(new(0, 0)));
        Assert.False(cache.TryGet(new(1, 0), out _));
        Assert.True(cache.IsCurrent(new(2, 0)));
    }

    /// <summary>Idle images are evicted before images with an active or pending rebuild.</summary>
    [Fact]
    public async Task Retention_EvictsIdleImagesBeforePendingOrBuilding()
    {
        var source = new TestWorldMapSource(new(0f, 0f, 384f, 64f));
        using var cache = new WorldMapChunkCache(source, Style, 2);
        Build(cache, new(0, 0));
        Build(cache, new(1, 0));
        cache.Invalidate(new(31f, 31f, 1f, 1f));
        cache.Invalidate(new(95f, 31f, 1f, 1f));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        source.BeforeSample = area =>
        {
            if (area.X >= 0f) return;
            entered.Set();
            if (!release.Wait(Timeout)) throw new TimeoutException("Build was not released.");
        };
        Task<bool> building = Task.Run(cache.BuildNext);
        try
        {
            Assert.True(entered.Wait(Timeout));
            cache.Request(new(2, 0), -1f);
            Assert.True(cache.BuildNext());
            Assert.True(cache.TryGet(new(0, 0), out _));
            Assert.True(cache.TryGet(new(1, 0), out _));
            Assert.False(cache.TryGet(new(2, 0), out _));
            Assert.Equal(2, cache.RetainedCount);
        }
        finally
        {
            release.Set();
            await building.WaitAsync(Timeout);
        }
        Assert.True(cache.IsCurrent(new(0, 0)));
        Assert.True(cache.BuildNext());
        Assert.True(cache.IsCurrent(new(1, 0)));
    }

    /// <summary>Dropping an active rebuild's old image preserves its entry and eventual current publication.</summary>
    [Fact]
    public async Task Retention_EvictedBuildingImage_StillPublishesCurrentChunk()
    {
        var source = new TestWorldMapSource(new(0f, 0f, 384f, 64f));
        using var cache = new WorldMapChunkCache(source, Style, 1);
        Build(cache, new(0, 0));
        Assert.True(cache.TryGet(new(0, 0), out var original));
        cache.Invalidate(new(31f, 31f, 1f, 1f));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        source.BeforeSample = area =>
        {
            if (area.X >= 0f)
            {
                cache.Invalidate(new(223f, 31f, 1f, 1f));
                return;
            }
            entered.Set();
            if (!release.Wait(Timeout)) throw new TimeoutException("Build was not released.");
        };
        Task<bool> building = Task.Run(cache.BuildNext);
        try
        {
            Assert.True(entered.Wait(Timeout));
            cache.Request(new(3, 0), -1f);
            Assert.True(cache.BuildNext());
            Assert.Equal(1, cache.RetainedCount);
            Assert.False(cache.TryGet(new(0, 0), out _));
            Assert.False(cache.IsCurrent(new(0, 0)));
            Assert.True(cache.TryGet(new(3, 0), out _));
            Assert.False(cache.IsCurrent(new(3, 0)));

            source.BeforeSample = null;
            cache.Request(new(0, 0), 0f);
            Assert.True(cache.BuildNext());
            Assert.True(cache.IsCurrent(new(3, 0)));
            Assert.Equal(1, cache.RetainedCount);
            Assert.False(cache.TryGet(new(0, 0), out _));
            Assert.False(cache.BuildNext());
        }
        finally
        {
            release.Set();
            Assert.True(await building.WaitAsync(Timeout));
        }
        Assert.True(cache.TryGet(new(0, 0), out var rebuilt));
        Assert.NotSame(original, rebuilt);
        Assert.True(rebuilt.Generation > original.Generation);
        Assert.True(cache.IsCurrent(new(0, 0)));
        Assert.False(cache.TryGet(new(3, 0), out _));
        Assert.Equal(1, cache.RetainedCount);
        Assert.False(cache.BuildNext());
    }

    /// <summary>Eviction removes the entry, but later requests never reuse its published generations.</summary>
    [Fact]
    public void Retention_EvictedChunkRebuildsOnRequest()
    {
        using var cache = Cache(1);
        Build(cache, new(0, 0));
        Assert.True(cache.TryGet(new(0, 0), out var original));
        cache.Invalidate(new(31f, 31f, 1f, 1f));
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(new(0, 0), out var old));
        Assert.True(old.Generation > original.Generation);
        Build(cache, new(3, 0));
        Assert.False(cache.TryGet(new(0, 0), out _));
        cache.Invalidate(new(31f, 31f, 1f, 1f));
        Build(cache, new(0, 0));
        Assert.True(cache.TryGet(new(0, 0), out var rebuilt));
        Assert.NotSame(old, rebuilt);
        Assert.True(rebuilt.Generation > old.Generation);
        Assert.True(cache.IsCurrent(new(0, 0)));
        Assert.Equal(1, cache.RetainedCount);
    }

    /// <summary>Pending images obey the cap immediately and their queued rebuilds still run after eviction.</summary>
    [Fact]
    public void Retention_FailedRebuilds_TrimProtectedOverflow()
    {
        var source = new TestWorldMapSource(new(0f, 0f, 128f, 64f));
        using var cache = new WorldMapChunkCache(source, Style, 1);
        Build(cache, new(0, 0));
        cache.Invalidate(new(31f, 31f, 1f, 1f));
        cache.Request(new(1, 0), -1f);
        source.BeforeSample = _ => cache.Invalidate(new(95f, 31f, 1f, 1f));
        Assert.True(cache.BuildNext());
        Assert.Equal(1, cache.RetainedCount);
        Assert.False(cache.TryGet(new(0, 0), out _));
        Assert.True(cache.TryGet(new(1, 0), out _));
        Assert.False(cache.IsCurrent(new(0, 0)));
        Assert.False(cache.IsCurrent(new(1, 0)));

        source.BeforeSample = _ => throw new InvalidOperationException("Rebuild failure.");
        Assert.True(cache.BuildNext());
        int afterFirstFailure = cache.RetainedCount;
        Assert.True(cache.IsFailed(new(1, 0)));
        Assert.True(cache.BuildNext());
        Assert.True(cache.IsFailed(new(0, 0)));
        Assert.False(cache.BuildNext());

        Assert.Equal(1, afterFirstFailure);
        Assert.Equal(1, cache.RetainedCount);
        Assert.False(cache.TryGet(new(0, 0), out _));
        Assert.True(cache.IsFailed(new(1, 0)));
    }

    /// <summary>The retention cap must permit at least one built image.</summary>
    [Fact]
    public void Retention_InvalidCap_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Cache(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Cache(-1));
    }

    /// <summary>A whole-world coarse request and fifty native requests drain to the configured cap.</summary>
    [Fact]
    public void FitWholeLargeWorld_RetainsAtMostCap()
    {
        var source = new TestWorldMapSource(new(0f, 0f, 6400f, 6400f), 64f);
        using var cache = new WorldMapChunkCache(source, Style, 16);
        foreach (WorldMapChunkCoord chunk in WorldMapChunking.ChunksTouching(source.Bounds, 64f,
            WorldMapChunking.MaxLevel(source.Bounds, 64f)))
            cache.Request(chunk, -1f);
        for (int x = 0; x < 50; x++) cache.Request(new(x, 0), x);
        int built = 0;
        while (cache.BuildNext()) built++;
        Assert.Equal(51, built);
        Assert.Equal(16, cache.RetainedCount);
    }

    static WorldMapChunkCache Cache(int cap) =>
        new(new TestWorldMapSource(new(0f, 0f, 384f, 64f)), Style, cap);

    static void Build(WorldMapChunkCache cache, WorldMapChunkCoord chunk)
    {
        cache.Request(chunk, 0f);
        Assert.True(cache.BuildNext());
    }
}
