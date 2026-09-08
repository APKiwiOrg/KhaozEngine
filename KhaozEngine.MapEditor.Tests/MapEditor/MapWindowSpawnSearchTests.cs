using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

public sealed class MapWindowSpawnSearchTests
{
    [Fact]
    public void Nearest_search_storage_is_bounded_by_the_requested_candidates()
    {
        var entries = Enumerable.Range(0, 100_000)
            .Select(index => new MapTileEntry(new MapTileCoord(100_000 - index, 0), "hash", false)).ToArray();
        _ = MapWindowSpawnSearch.Nearest(entries, default, 32);

        long before = GC.GetAllocatedBytesForCurrentThread();
        MapTileEntry[] nearest = MapWindowSpawnSearch.Nearest(entries, default, 32);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(Enumerable.Range(1, 32), nearest.Select(entry => entry.Coord.X));
        Assert.True(allocated < 32_768, $"A 32-candidate search allocated {allocated} bytes.");
    }
}
