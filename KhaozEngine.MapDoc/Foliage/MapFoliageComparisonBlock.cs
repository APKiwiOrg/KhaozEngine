using System;
using System.Collections.Generic;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>Owns 64 by 64 integer candidate addresses, independent of either proposal's world rectangles.</summary>
public readonly record struct MapFoliageComparisonBlock(int X, int Z)
{
    public static MapFoliageComparisonBlock For(int x, int z) => new(Floor(x), Floor(z));
    static int Floor(int v) => v / 64 - (v % 64 < 0 ? 1 : 0);
}

public enum MapFoliageChangeKind { Added, Removed, Changed }
public sealed record MapFoliageChange(MapFoliageComparisonAddress Address, MapFoliageChangeKind Kind,
    MapFoliageDecision? Before, MapFoliageDecision? After);

/// <summary>Only Complete issues a digest binding the before identity and proposal. Observations remain provisional.</summary>
public sealed record MapFoliageDifferentialResult(MapFoliageStatus Status, long Added, long Removed, long Changed,
    string Digest, string BeforeIdentity, string ProposalDigest, MapFoliageWork Work);

/// <summary>The inclusive released lattice range. Wide counts admit work before an iterator or area allocation.</summary>
internal readonly record struct MapFoliageLatticeRange(int MinX, int MinZ, int MaxX, int MaxZ)
{
    internal bool Contains(int x, int z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
    internal decimal Count => ((decimal)MaxX - MinX + 1) * ((decimal)MaxZ - MinZ + 1);
    internal decimal Intersection(MapFoliageLatticeRange b) => Math.Max(0m, (decimal)Math.Min(MaxX, b.MaxX) - Math.Max(MinX, b.MinX) + 1) *
        Math.Max(0m, (decimal)Math.Min(MaxZ, b.MaxZ) - Math.Max(MinZ, b.MinZ) + 1);
    internal IEnumerable<MapFoliageComparisonBlock> Blocks()
    {
        var lo = MapFoliageComparisonBlock.For(MinX, MinZ);
        var hi = MapFoliageComparisonBlock.For(MaxX, MaxZ);
        for (long z = lo.Z; z <= hi.Z; z++)
            for (long x = lo.X; x <= hi.X; x++) yield return new((int)x, (int)z);
    }
    internal static MapFoliageLatticeRange Of(float minX, float minZ, float maxX, float maxZ, float spacing) =>
        new(Bound(Math.Floor(minX / spacing - 0.5f)), Bound(Math.Floor(minZ / spacing - 0.5f)),
            Bound(Math.Ceiling(maxX / spacing + 0.5f)), Bound(Math.Ceiling(maxZ / spacing + 0.5f)));
    static int Bound(double n) => double.IsFinite(n) && n >= int.MinValue && n <= int.MaxValue ? (int)n :
        throw new MapFoliageRefusal(MapFoliageStatus.NotRepresentable, MapFoliageSupport.NoWork, "candidate lattice coordinates");
}
