// Frozen allocating reference after the #1712 F1 metric correction for 23.3.0.
// Keep independent of production scratch. Change only with an evidenced reference update.
using System;

using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Navigation;

/// <summary>A containing box of admitted goal cells in a frame shared by every layer.</summary>
internal readonly record struct ReferencePlannerRegionBounds(int MinX, int MinZ, int MaxX, int MaxZ)
{
    internal static ReferencePlannerRegionBounds Empty => new(int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);

    internal ReferencePlannerRegionBounds Include(int x, int z)
        => new(Math.Min(MinX, x), Math.Min(MinZ, z), Math.Max(MaxX, x), Math.Max(MaxZ, z));

    internal (int X, int Z) Closest(int x, int z)
        => (Math.Max(MinX, Math.Min(MaxX, x)), Math.Max(MinZ, Math.Min(MaxZ, z)));
}
