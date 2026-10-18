using System;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Markers;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapEditor;
using Xunit;

namespace KhaozEngine.Tests.MapEditor;

[Collection("AllocSensitive")]
public sealed class MapWindowSpawnSearchTests
{
    [Fact]
    public void Nearest_search_storage_is_bounded_by_the_requested_candidates()
    {
        var entries = Enumerable.Range(0, 100_000)
            .Select(index => new MapTileEntry(new MapTileCoord(100_000 - index, 0), "hash", false)).ToArray();
        _ = MapWindowSpawnSearch.Nearest(entries, default, 32);

        MapTileEntry[] nearest = [];
        long allocated = AllocationMeasurement.Measure("A 32-candidate search",
            () => nearest = MapWindowSpawnSearch.Nearest(entries, default, 32));

        Assert.Equal(Enumerable.Range(1, 32), nearest.Select(entry => entry.Coord.X));
        Assert.True(allocated < 32_768, $"A 32-candidate search allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData(-64.25f, -2)]
    [InlineData(-64f, -1)]
    [InlineData(-0.25f, -1)]
    [InlineData(0f, 0)]
    [InlineData(63.75f, 0)]
    [InlineData(64f, 1)]
    public void Native_anchor_tiles_floor_like_the_search_grid(float x, int tile)
    {
        var position = new MapExactXz(MapExactValue.FromSingle(x), MapExactValue.FromSingle(-x));
        MapTileCoord anchor = MapMarkerAnchors.TileOf(position, 64);
        Assert.Equal(tile, anchor.X);
        Assert.Equal(MapTileGrid.CoordOf(x, -x, 64), anchor);
    }
}
