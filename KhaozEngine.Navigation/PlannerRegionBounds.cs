using System;

namespace KhaozEngine.Navigation;

/// <summary>A containing box of admitted goal cells in a frame shared by every layer.</summary>
internal readonly record struct PlannerRegionBounds(int MinX, int MinZ, int MaxX, int MaxZ)
{
    internal static PlannerRegionBounds Empty => new(int.MaxValue, int.MaxValue, int.MinValue, int.MinValue);

    internal PlannerRegionBounds Include(int x, int z)
        => new(Math.Min(MinX, x), Math.Min(MinZ, z), Math.Max(MaxX, x), Math.Max(MaxZ, z));

    internal (int X, int Z) Closest(int x, int z)
        => (Math.Max(MinX, Math.Min(MaxX, x)), Math.Max(MinZ, Math.Min(MaxZ, z)));
}
