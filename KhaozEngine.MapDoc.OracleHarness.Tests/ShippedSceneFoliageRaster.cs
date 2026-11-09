using System;

namespace KhaozEngine.Tests.MapDocOracle;

internal static partial class ShippedSceneFoliageRoster
{
    // Search the monotone sampler-domain predicates before enumerating any owner. These are the released
    // binary32 operations, including the closed last raster point and the half-open chunk's final float.
    internal static (int First, int Last) RasterOwners(float origin, float cell, int size, int first, int last)
    {
        long end = (long)last + 1;
        long begin = LowerBound(first, end, k => (MathF.BitDecrement(Edge(origin, cell, k + 1)) - origin) / cell >= 0);
        long after = LowerBound(begin, end, k => (Edge(origin, cell, k) - origin) / cell > size - 1);
        return begin >= after ? (0, -1) : ((int)begin, (int)(after - 1));
    }

    static long LowerBound(long first, long end, Func<long, bool> predicate)
    {
        while (first < end)
        {
            long middle = first + (end - first) / 2;
            if (predicate(middle)) end = middle;
            else first = middle + 1;
        }
        return first;
    }
}
