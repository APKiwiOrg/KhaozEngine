using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>Orders completed builds without creating a comparison closure during an idle queue pump.</summary>
internal static class TileWorldBuildOrder<TOutput>
{
    public static List<TileWorldBuildResult<TOutput>> Sort(
        ICollection<TileWorldBuildResult<TOutput>> ready, RegionCoord focus)
    {
        var ordered = new List<TileWorldBuildResult<TOutput>>(ready);
        ordered.Sort((a, b) => NearestFirst(a, b, focus));
        return ordered;
    }

    static int NearestFirst(TileWorldBuildResult<TOutput> a, TileWorldBuildResult<TOutput> b, RegionCoord focus)
    {
        int ad = Distance(a.Key.Region, focus);
        int bd = Distance(b.Key.Region, focus);
        if (ad != bd) return ad.CompareTo(bd);
        if (a.Key.Region.Rz != b.Key.Region.Rz) return a.Key.Region.Rz.CompareTo(b.Key.Region.Rz);
        if (a.Key.Region.Rx != b.Key.Region.Rx) return a.Key.Region.Rx.CompareTo(b.Key.Region.Rx);
        if (a.Key.Plane != b.Key.Plane) return a.Key.Plane.CompareTo(b.Key.Plane);
        if (a.Key.Kind != b.Key.Kind) return a.Key.Kind.CompareTo(b.Key.Kind);
        return string.CompareOrdinal(a.Key.LayerId, b.Key.LayerId);
    }

    static int Distance(RegionCoord region, RegionCoord focus) =>
        Math.Max(Math.Abs(region.Rx - focus.Rx), Math.Abs(region.Rz - focus.Rz));
}
