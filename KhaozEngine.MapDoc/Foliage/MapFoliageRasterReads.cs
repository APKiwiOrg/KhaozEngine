using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>The rounded generation rectangles and the density samples their owned binary32 points can read. The
/// released float subtraction and division are monotone, so the first and last owned floats bound every bilinear read.
/// Readers invert those same intervals, including a preceding sample read at a rounded lower edge.</summary>
internal static class MapFoliageRasterReads
{
    internal static float Edge(MapFoliageLayer layer, long index, bool x) =>
        (float)((x ? layer.OriginX : layer.OriginZ) + index * (32.0 * layer.CellSize));

    internal static int Owner(MapFoliageLayer layer, float point, bool x)
    {
        double approximate = Math.Floor(((double)point - (x ? layer.OriginX : layer.OriginZ)) / (32.0 * layer.CellSize));
        if (approximate >= int.MinValue && approximate <= int.MaxValue)
        {
            int key = (int)approximate;
            if (Edge(layer, key, x) <= point && point < Edge(layer, (long)key + 1, x)) return key;
        }
        // Find the last rounded lower edge at or below the point. Equal edges own no floats, so this also skips every
        // collapsed rectangle without a linear walk, even near the limits of the logical index domain.
        long after = First(i => Edge(layer, i, x) > point);
        long owner = after - 1;
        if (owner < int.MinValue || owner > int.MaxValue || !(point < Edge(layer, owner + 1, x)))
            throw MapFoliageDistribution.Refuse(MapFoliageStatus.NotRepresentable, "logical chunk index");
        return (int)owner;
    }

    internal static IEnumerable<MapFoliageChunkKey> Density(MapFoliageLayer layer, MapFoliageChunkKey chunk)
    {
        var xs = Samples(layer, chunk.X, true);
        var zs = Samples(layer, chunk.Z, false);
        if (xs is null || zs is null) yield break;
        for (int z = zs.Value.Min / 32; z <= zs.Value.Max / 32; z++)
            for (int x = xs.Value.Min / 32; x <= xs.Value.Max / 32; x++) yield return new(x, z);
    }

    internal static IEnumerable<MapFoliageChunkKey> Readers(MapFoliageLayer layer, MapFoliageChunkKey density)
    {
        int[] xs = AxisReaders(layer, density.X, true).ToArray();
        foreach (int z in AxisReaders(layer, density.Z, false))
            foreach (int x in xs) yield return new(x, z);
    }

    internal static IEnumerable<int> Chunks(MapFoliageLayer layer, int first, int last, bool x)
    {
        for (long i = first; i <= last;)
        {
            yield return (int)i;
            if (i == last) yield break;
            i = Owner(layer, Edge(layer, i + 1, x), x);
        }
    }

    static (int Min, int Max)? Samples(MapFoliageLayer layer, int chunk, bool x)
    {
        float first = Edge(layer, chunk, x), last = MathF.BitDecrement(Edge(layer, (long)chunk + 1, x));
        if (!float.IsFinite(first) || !float.IsFinite(last) || first > last) return null;
        float low = Coordinate(layer, first, x), high = Coordinate(layer, last, x);
        int end = (x ? layer.Width : layer.Depth) - 1;
        if (high < 0 || low > end) return null;
        return ((int)MathF.Floor(MathF.Max(0, low)), Math.Min((int)MathF.Floor(MathF.Min(end, high)) + 1, end));
    }

    static IEnumerable<int> AxisReaders(MapFoliageLayer layer, int density, bool x)
    {
        long min = 32L * density;
        long max = Math.Min(min + 31, (x ? layer.Width : layer.Depth) - 1L);
        if (min < 0 || min > max) yield break;
        long first = First(i => MathF.Floor(Coordinate(layer, MathF.BitDecrement(Edge(layer, i + 1, x)), x)) + 1 >= min);
        long after = First(i => MathF.Floor(Coordinate(layer, Edge(layer, i, x), x)) > max);
        for (long i = first; i < after && i <= int.MaxValue;)
        {
            if (Samples(layer, (int)i, x) is { } samples && samples.Min <= max && samples.Max >= min) yield return (int)i;
            float edge = Edge(layer, i + 1, x);
            if (i == int.MaxValue || !float.IsFinite(edge)) yield break;
            i = Math.Max(i + 1, Owner(layer, edge, x));
        }
    }

    static float Coordinate(MapFoliageLayer layer, float point, bool x) =>
        MapFoliageSupport.RasterCoordinate(point, x ? layer.OriginX : layer.OriginZ, layer.CellSize);

    // Lower bound over every representable logical chunk index, with an exclusive sentinel one past int.MaxValue.
    static long First(Func<long, bool> predicate)
    {
        long low = int.MinValue, high = (long)int.MaxValue + 1;
        while (low < high)
        {
            long middle = low + (high - low) / 2;
            if (predicate(middle)) high = middle;
            else low = middle + 1;
        }
        return low;
    }
}
