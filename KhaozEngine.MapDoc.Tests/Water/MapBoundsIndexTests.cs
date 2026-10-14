using System;
using System.Linq;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

/// <summary>SF-R1 M2. The cached parent index of the occupied partition's room stage.</summary>
public sealed class MapBoundsIndexTests
{
    static MapWideXz P(long x, long z) => new(MapWideRational.From(x), MapWideRational.From(z));

    [Fact]
    public void BoundsIndex_ReturnsEveryOpenlyOverlappingBoxInOrder()
    {
        var random = new Random(20261014);
        MapWaterPolygons.Bounds Box()
        {
            long x = random.Next(0, 60), z = random.Next(0, 60);
            return MapWaterPolygons.Bounds.Of(P(x, z), P(x + random.Next(0, 6), z + random.Next(0, 6)));
        }
        var boxes = Enumerable.Range(0, 500).Select(_ => Box()).ToArray();
        var index = new MapBoundsIndex(boxes);
        int found = 0;
        for (int q = 0; q < 200; q++)
        {
            var query = Box();
            var expected = Enumerable.Range(0, boxes.Length).Where(i => boxes[i].OverlapsOpen(query)).ToList();
            found += expected.Count;
            Assert.Equal(expected, index.Overlapping(query));
        }
        Assert.True(found > 0);
        Assert.Empty(new MapBoundsIndex([]).Overlapping(Box()));
    }
}
