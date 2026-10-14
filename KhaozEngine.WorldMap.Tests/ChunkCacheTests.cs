using System;
using System.Threading;
using System.Threading.Tasks;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Proves priority ordering, generation races and failure recovery for cached chunks.</summary>
public sealed class ChunkCacheTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    static readonly WorldMapStyle Style = new() { ChunkMetres = 8f };

    /// <summary>A requested chunk publishes raster bytes and feature anchors as one current generation.</summary>
    [Fact]
    public void Request_ThenBuildNext_PublishesCurrentChunk()
    {
        var source = Source();
        source.Revision = 7;
        source.Markers.Add(new("bank", new(4f, 4f), ["bank"]));
        using var cache = new WorldMapChunkCache(source, Style with
        {
            IconRules = [new(WorldMapFeatureSource.Marker, ["bank"], "bank", 1)]
        });
        Assert.False(cache.TryGet(new(0, 0), out var missing));
        Assert.Null(missing);
        Assert.False(cache.IsCurrent(new(0, 0)));
        Assert.False(cache.IsFailed(new(0, 0)));

        cache.Request(new(0, 0), 1f);

        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(new(0, 0), out var built));
        Assert.Equal(new WorldMapChunkCoord(0, 0), built.Image.Coord);
        Assert.Equal(32, built.Image.Size);
        Assert.Equal(32 * 32 * 4, built.Image.Rgba.Length);
        Assert.Equal(7, built.Image.Revision);
        Assert.Equal("bank", Assert.Single(built.Features.Icons).IconKey);
        Assert.True(cache.IsCurrent(new(0, 0)));
        Assert.False(cache.BuildNext());
    }

    /// <summary>The smaller priority is selected first regardless of request order.</summary>
    [Fact]
    public void BuildNext_TakesLowestPriorityFirst()
    {
        using var cache = new WorldMapChunkCache(Source(), Style);
        cache.Request(new(1, 0), 5f);
        cache.Request(new(0, 0), 1f);

        Assert.True(cache.BuildNext());

        Assert.True(cache.IsCurrent(new(0, 0)));
        Assert.False(cache.IsCurrent(new(1, 0)));
        Assert.True(cache.BuildNext());
        Assert.True(cache.IsCurrent(new(1, 0)));
    }

    /// <summary>A newer request replaces the pending priority in either direction.</summary>
    [Theory]
    [InlineData(1f, 10f, 1)]
    [InlineData(10f, 1f, 0)]
    public void Request_LatestPriorityWins(float original, float latest, int firstX)
    {
        using var cache = new WorldMapChunkCache(Source(), Style);
        cache.Request(new(0, 0), original);
        cache.Request(new(1, 0), 5f);
        cache.Request(new(0, 0), latest);

        Assert.True(cache.BuildNext());

        Assert.True(cache.IsCurrent(new(firstX, 0)));
        Assert.False(cache.IsCurrent(new(1 - firstX, 0)));
    }

    /// <summary>Current chunks are not queued again by per-frame requests.</summary>
    [Fact]
    public void Request_CurrentChunk_IsIgnored()
    {
        var source = Source();
        using var cache = new WorldMapChunkCache(source, Style);
        cache.Request(new(0, 0), 1f);
        Assert.True(cache.BuildNext());

        cache.Request(new(0, 0), 0f);

        Assert.False(cache.BuildNext());
        Assert.Equal(1, source.SampleCalls);
    }

    /// <summary>Chunks beyond or only touching the authored bounds are ignored.</summary>
    [Fact]
    public void Request_OutsideBounds_IsIgnored()
    {
        var source = Source();
        using var cache = new WorldMapChunkCache(source, Style);
        foreach (WorldMapChunkCoord chunk in new WorldMapChunkCoord[]
        {
            new(-1, 0), new(3, 0), new(0, -1), new(0, 1), new(20, 20)
        })
            cache.Request(chunk, 1f);

        Assert.False(cache.BuildNext());
        Assert.Equal(0, source.SampleCalls);
    }

    /// <summary>An empty source cannot queue a chunk even if its origin lies inside that chunk.</summary>
    [Theory]
    [InlineData(0f, 8f)]
    [InlineData(8f, 0f)]
    [InlineData(0f, 0f)]
    public void Request_ZeroAreaSource_IsIgnored(float width, float height)
    {
        using var cache = new WorldMapChunkCache(new TestWorldMapSource(new(4f, 4f, width, height)), Style);
        cache.Request(new(0, 0), 1f);

        Assert.False(cache.BuildNext());
    }

    /// <summary>Any positive overlap, including at negative coordinates, permits a request.</summary>
    [Fact]
    public void Request_PartiallyInsideBounds_IsBuilt()
    {
        using var cache = new WorldMapChunkCache(new TestWorldMapSource(new(-1f, -1f, 2f, 2f)), Style);
        cache.Request(new(-1, -1), 1f);

        Assert.True(cache.BuildNext());
        Assert.True(cache.IsCurrent(new(-1, -1)));
    }

    /// <summary>A middle-of-chunk change keeps the two neighbours current.</summary>
    [Fact]
    public void Invalidate_MarksOnlyTouchedChunksPlusMargin()
    {
        using var cache = new WorldMapChunkCache(Source(), Style);
        BuildRow(cache);

        cache.Invalidate(new(11.5f, 3.5f, 1f, 1f));

        Assert.True(cache.IsCurrent(new(0, 0)));
        Assert.False(cache.IsCurrent(new(1, 0)));
        Assert.True(cache.IsCurrent(new(2, 0)));
    }

    /// <summary>The invalidation reach crosses an adjacent chunk edge.</summary>
    [Fact]
    public void Invalidate_AtChunkEdge_ReachesNeighbourThroughMargin()
    {
        using var cache = new WorldMapChunkCache(Source(), Style);
        BuildRow(cache);

        cache.Invalidate(new(7f, 3.5f, 1f, 1f));

        Assert.False(cache.IsCurrent(new(0, 0)));
        Assert.False(cache.IsCurrent(new(1, 0)));
        Assert.True(cache.IsCurrent(new(2, 0)));
    }

    /// <summary>Shoreline pixels, wall width and the extra footprint pixel all extend invalidation.</summary>
    [Fact]
    public void Invalidate_IncludesFullRasterReach()
    {
        using var cache = new WorldMapChunkCache(Source(), Style);
        BuildRow(cache);

        // The old cell-plus-shadow margin stops before x=8. The full reach crosses it.
        cache.Invalidate(new(5f, 3.5f, 1f, 1f));

        Assert.False(cache.IsCurrent(new(1, 0)));
        Assert.True(cache.IsCurrent(new(2, 0)));
    }

    /// <summary>A build interrupted by invalidation publishes its captured generation as stale.</summary>
    [Fact]
    public async Task Invalidate_DuringBuild_LeavesChunkStale()
    {
        var source = Source();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        source.BeforeSample = _ =>
        {
            entered.Set();
            if (!release.Wait(Timeout)) throw new TimeoutException("Build was not released.");
        };
        using var cache = new WorldMapChunkCache(source, Style);
        cache.Request(new(0, 0), 1f);
        Task<bool> build = Task.Run(cache.BuildNext);
        try
        {
            Assert.True(entered.Wait(Timeout));
            cache.Invalidate(new(3.5f, 3.5f, 1f, 1f));
        }
        finally
        {
            release.Set();
        }

        Assert.True(await build.WaitAsync(Timeout));
        Assert.True(cache.TryGet(new(0, 0), out var stale));
        Assert.False(cache.IsCurrent(new(0, 0)));
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(new(0, 0), out var rebuilt));
        Assert.Equal(stale.Generation + 1, rebuilt.Generation);
        Assert.True(cache.IsCurrent(new(0, 0)));
    }

    /// <summary>A per-frame request during a build cannot launch a second build of the same chunk.</summary>
    [Fact]
    public async Task Request_DuringBuild_DoesNotDuplicateBuild()
    {
        var source = Source();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        source.BeforeSample = _ =>
        {
            entered.Set();
            if (!release.Wait(Timeout)) throw new TimeoutException("Build was not released.");
        };
        using var cache = new WorldMapChunkCache(source, Style);
        cache.Request(new(0, 0), 1f);
        Task<bool> build = Task.Run(cache.BuildNext);
        try
        {
            Assert.True(entered.Wait(Timeout));
            cache.Request(new(0, 0), 0f);
            Assert.False(cache.BuildNext());
            Assert.False(cache.TryGet(new(0, 0), out _));
        }
        finally
        {
            release.Set();
        }

        Assert.True(await build.WaitAsync(Timeout));
        Assert.True(cache.IsCurrent(new(0, 0)));
        Assert.False(cache.BuildNext());
        Assert.Equal(1, source.SampleCalls);
    }

    /// <summary>The source event invalidates automatically and disposal detaches the subscriber.</summary>
    [Fact]
    public void SourceChanged_InvalidatesViaSubscription_AndDisposeUnsubscribes()
    {
        var source = Source();
        using var cache = new WorldMapChunkCache(source, Style);
        cache.Request(new(0, 0), 1f);
        Assert.True(cache.BuildNext());

        source.RaiseChanged(new(3.5f, 3.5f, 1f, 1f));
        Assert.False(cache.IsCurrent(new(0, 0)));
        Assert.True(cache.BuildNext());
        cache.Dispose();
        source.RaiseChanged(new(3.5f, 3.5f, 1f, 1f));

        Assert.True(cache.IsCurrent(new(0, 0)));
    }

    /// <summary>A source exception quarantines only the failed chunk until a touching invalidation.</summary>
    [Fact]
    public void SourceThrows_ChunkFailed_NotRetriedUntilInvalidated()
    {
        var source = Source();
        source.BeforeSample = _ => throw new InvalidOperationException("Test source failure.");
        using var cache = new WorldMapChunkCache(source, Style);
        cache.Request(new(0, 0), 1f);

        Assert.True(cache.BuildNext());
        Assert.True(cache.IsFailed(new(0, 0)));
        Assert.False(cache.IsCurrent(new(0, 0)));
        cache.Request(new(0, 0), 0f);
        Assert.False(cache.BuildNext());
        Assert.Equal(1, source.SampleCalls);
        cache.Invalidate(new(19.5f, 3.5f, 1f, 1f));
        Assert.True(cache.IsFailed(new(0, 0)));
        Assert.False(cache.BuildNext());

        source.BeforeSample = null;
        cache.Invalidate(new(3.5f, 3.5f, 1f, 1f));
        Assert.False(cache.IsFailed(new(0, 0)));
        Assert.True(cache.BuildNext());
        Assert.True(cache.IsCurrent(new(0, 0)));
        Assert.Equal(2, source.SampleCalls);
    }

    /// <summary>An older build failure cannot quarantine a generation invalidated while it was running.</summary>
    [Fact]
    public async Task Invalidate_DuringFailingBuild_DoesNotFailNewGeneration()
    {
        var source = Source();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        source.BeforeSample = _ =>
        {
            entered.Set();
            if (!release.Wait(Timeout)) throw new TimeoutException("Build was not released.");
            throw new InvalidOperationException("Old generation failure.");
        };
        using var cache = new WorldMapChunkCache(source, Style);
        cache.Request(new(0, 0), 1f);
        Task<bool> build = Task.Run(cache.BuildNext);
        try
        {
            Assert.True(entered.Wait(Timeout));
            cache.Invalidate(new(3.5f, 3.5f, 1f, 1f));
        }
        finally
        {
            release.Set();
        }

        Assert.True(await build.WaitAsync(Timeout));
        Assert.False(cache.IsFailed(new(0, 0)));
        source.BeforeSample = null;
        Assert.True(cache.BuildNext());
        Assert.True(cache.IsCurrent(new(0, 0)));
    }

    /// <summary>Invalidation preserves the previous chunk until a newer generation is published.</summary>
    [Fact]
    public void StaleChunk_StaysReadableUntilRebuilt()
    {
        var source = Source();
        using var cache = new WorldMapChunkCache(source, Style);
        cache.Request(new(0, 0), 1f);
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(new(0, 0), out var original));

        cache.Invalidate(new(3.5f, 3.5f, 1f, 1f));

        Assert.True(cache.TryGet(new(0, 0), out var stale));
        Assert.Same(original, stale);
        source.Revision = 8;
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(new(0, 0), out var rebuilt));
        Assert.NotSame(original, rebuilt);
        Assert.Equal(original.Generation + 1, rebuilt.Generation);
        Assert.Equal(8, rebuilt.Image.Revision);
        Assert.True(cache.IsCurrent(new(0, 0)));
    }

    /// <summary>A failed rebuild preserves the last readable chunk.</summary>
    [Fact]
    public void FailedRebuild_PreservesStaleChunk()
    {
        var source = Source();
        using var cache = new WorldMapChunkCache(source, Style);
        cache.Request(new(0, 0), 1f);
        Assert.True(cache.BuildNext());
        Assert.True(cache.TryGet(new(0, 0), out var original));
        cache.Invalidate(new(3.5f, 3.5f, 1f, 1f));
        source.BeforeSample = _ => throw new InvalidOperationException("Rebuild failure.");

        Assert.True(cache.BuildNext());

        Assert.True(cache.TryGet(new(0, 0), out var stale));
        Assert.Same(original, stale);
        Assert.True(cache.IsFailed(new(0, 0)));
        Assert.False(cache.IsCurrent(new(0, 0)));
    }

    static TestWorldMapSource Source() => new(new(0f, 0f, 24f, 8f), cellSize: 0.5f);

    static void BuildRow(WorldMapChunkCache cache)
    {
        for (int x = 0; x < 3; x++)
        {
            cache.Request(new(x, 0), x);
            Assert.True(cache.BuildNext());
        }
    }
}
