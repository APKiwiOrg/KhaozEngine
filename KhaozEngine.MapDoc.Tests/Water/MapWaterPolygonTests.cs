using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Water;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Water;

public class MapWaterPolygonTests
{
    static MapWideRational W(long value) => MapWideRational.From(value);
    static MapWideXz P(long x, long z) => new(W(x), W(z));
    static MapWidePoint P3(long x, long y, long z) => new(W(x), W(y), W(z));
    static MapWideXz[] Ring(params (long X, long Z)[] points) => points.Select(p => P(p.X, p.Z)).ToArray();
    static MapWideXz[] Square(long minX, long minZ, long maxX, long maxZ) => Ring((minX, minZ), (maxX, minZ), (maxX, maxZ), (minX, maxZ));
    static MapWidePoint[] Flat(IReadOnlyList<MapWideXz> ring) => ring.Select(p => new MapWidePoint(p.X, default, p.Z)).ToArray();
    static MapWideRational Area(params IReadOnlyList<MapWideXz>[] polygons) => WaterFixtures.WideArea(polygons.Select(Flat).ToArray());

    static MapWideXz[] Shape(string name) => name switch
    {
        "l" => Ring((0, 0), (4, 0), (4, 4), (2, 4), (2, 2), (0, 2)),
        "straight-vertex" => Ring((0, 0), (2, 0), (4, 0), (4, 4), (2, 4), (2, 2), (0, 2)),
        // The ear at (2, 0) has the diagonal (0, 2) to (4, 2), which passes through the reflex vertex (2, 2).
        "reflex-on-diagonal" => Ring((2, 0), (4, 2), (4, 4), (2, 2), (0, 4), (0, 2)),
        _ => throw new ArgumentException("unknown shape", nameof(name)),
    };

    [Theory]
    [InlineData("l")]
    [InlineData("straight-vertex")]
    [InlineData("reflex-on-diagonal")]
    public void Triangulate_CoversTheRingExactly(string shape)
    {
        MapWideXz[] ring = Shape(shape);
        Assert.False(MapWaterPolygons.IsConvex(ring));
        IReadOnlyList<MapWideXz[]> triangles = Assert.IsAssignableFrom<IReadOnlyList<MapWideXz[]>>(
            MapWaterPolygons.Triangulate(ring, new MapWaterWorkBudget()));
        Assert.All(triangles, t => Assert.True(MapWaterPolygons.Cross(t[0], t[1], t[2]).Sign > 0));
        Assert.Equal(Area(ring), Area(triangles.ToArray<IReadOnlyList<MapWideXz>>()));
        Assert.DoesNotContain(triangles, t => t.Contains(P(0, 2)) && t.Contains(P(2, 0)) && t.Contains(P(4, 2)));
    }

    [Fact]
    public void Triangulate_ClockwiseRing_ReturnsNull() =>
        Assert.Null(MapWaterPolygons.Triangulate(Ring((0, 0), (0, 4), (4, 4), (4, 0)), new MapWaterWorkBudget()));

    [Fact]
    public void SplitAtHeight_PinsTheBoundary()
    {
        MapWideRational y = W(0);
        MapWidePoint[] touchingBelow = { P3(0, 0, 0), P3(4, -1, 0), P3(0, -1, 4) };
        var (above, rest) = MapWaterPolygons.SplitAtHeight(touchingBelow, y);
        Assert.Empty(above);
        Assert.Equal(touchingBelow, Assert.Single(rest).ToArray());

        MapWidePoint[] touchingAbove = { P3(0, 0, 0), P3(4, 1, 0), P3(0, 1, 4) };
        (above, rest) = MapWaterPolygons.SplitAtHeight(touchingAbove, y);
        Assert.Equal(touchingAbove, Assert.Single(above).ToArray());
        Assert.Empty(rest);

        MapWidePoint[] level = { P3(0, 0, 0), P3(4, 0, 0), P3(0, 0, 4) };
        (above, rest) = MapWaterPolygons.SplitAtHeight(level, y);
        Assert.Empty(above);
        Assert.Equal(level, Assert.Single(rest).ToArray());

        MapWidePoint[] straddling = { P3(0, 0, 0), P3(4, 2, 0), P3(0, -2, 4) };
        (above, rest) = MapWaterPolygons.SplitAtHeight(straddling, y);
        Assert.Contains(P3(0, 0, 0), Assert.Single(rest));
        Assert.All(Assert.Single(above), p => Assert.True(p.Y.Sign >= 0));
        Assert.Equal(WaterFixtures.WideArea(straddling), WaterFixtures.WideArea(above).Add(WaterFixtures.WideArea(rest)));
    }

    [Fact]
    public void ClipToRect_KeepsPositiveAreaOnly()
    {
        MapWideXz min = P(0, 0), max = P(4, 4);
        Assert.Equal(W(4), Area(MapWaterPolygons.ClipToRect(Square(2, 2, 6, 6), min, max)));
        Assert.Equal(W(16), Area(MapWaterPolygons.ClipToRect(Square(0, 0, 4, 4), min, max)));
        Assert.Equal(W(6), Area(MapWaterPolygons.ClipToRect(Ring((2, 0), (6, 0), (2, 4)), min, max)));
        Assert.Empty(MapWaterPolygons.ClipToRect(Square(4, 0, 6, 4), min, max));
        Assert.Empty(MapWaterPolygons.ClipToRect(Square(4, 4, 6, 6), min, max));
        Assert.Empty(MapWaterPolygons.ClipToRect(Square(-2, 4, 2, 6), min, max));
        Assert.Empty(MapWaterPolygons.ClipToRect(Square(5, 5, 6, 6), min, max));
    }

    [Fact]
    public void BoundsSweep_MatchesEveryOverlappingPair()
    {
        var random = new Random(20261011);
        var bounds = new MapWaterPolygons.Bounds[300];
        for (int i = 0; i < bounds.Length; i++)
        {
            long x = random.Next(0, 40), z = random.Next(0, 40);
            bounds[i] = MapWaterPolygons.Bounds.Of(P(x, z), P(x + random.Next(0, 6), z + random.Next(0, 6)));
        }
        foreach (bool touching in new[] { true, false })
        {
            var expected = new List<(int, int)>();
            for (int a = 0; a < bounds.Length; a++)
                for (int b = a + 1; b < bounds.Length; b++)
                    if (touching ? bounds[a].OverlapsClosed(bounds[b]) : bounds[a].OverlapsOpen(bounds[b])) expected.Add((a, b));
            var actual = MapWaterBoundsSweep.Pairs(bounds, touching, new MapWaterWorkBudget()).ToList();
            Assert.Equal(expected, actual.Order());
        }
    }

    [Fact]
    public void BoundsCross_MatchesEveryOverlappingCrossPairInOrder()
    {
        var random = new Random(20261012);
        MapWaterPolygons.Bounds Box()
        {
            long x = random.Next(0, 40), z = random.Next(0, 40);
            return MapWaterPolygons.Bounds.Of(P(x, z), P(x + random.Next(0, 6), z + random.Next(0, 6)));
        }
        var left = Enumerable.Range(0, 200).Select(_ => Box()).ToArray();
        var right = Enumerable.Range(0, 120).Select(_ => Box()).ToArray();
        var expected = new List<(int, int)>();
        for (int a = 0; a < left.Length; a++)
            for (int b = 0; b < right.Length; b++)
                if (left[a].OverlapsOpen(right[b])) expected.Add((a, b));
        Assert.NotEmpty(expected);
        Assert.Equal(expected, MapWaterBoundsSweep.Cross(left, right));
        Assert.Equal(expected.Select(p => (p.Item2, p.Item1)).Order(), MapWaterBoundsSweep.Cross(right, left));
        Assert.Empty(MapWaterBoundsSweep.Cross(left, []));
        // A result cap equal to the overlap count admits the input, one less refuses it with no partial result.
        Assert.Equal(expected, MapWaterBoundsSweep.Cross(left, right, expected.Count)!);
        Assert.Null(MapWaterBoundsSweep.Cross(left, right, expected.Count - 1));
    }

    [Fact]
    public void BoundsSweep_ColumnChargesTouchingNeighboursOnly()
    {
        var bounds = Enumerable.Range(0, 4096).Select(z => MapWaterPolygons.Bounds.Of(P(0, z), P(1, z + 1))).ToArray();
        var budget = new MapWaterWorkBudget();
        Assert.Empty(MapWaterBoundsSweep.Pairs(bounds, touching: false, budget));
        Assert.InRange(budget.Charged, 1, 2 * 4096);
        Assert.Equal(4095, MapWaterBoundsSweep.Pairs(bounds, touching: true, new MapWaterWorkBudget()).Count());
    }

    [Fact]
    public void Budget_RefusesOncePassed()
    {
        var budget = new MapWaterWorkBudget(2);
        budget.Charge(2);
        var error = Assert.Throws<MapDocumentException>(() => budget.Charge(1));
        Assert.Equal("domain work exceeds 2 pair checks", error.Message);
        Assert.True(budget.Exhausted);
    }
}
