using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileWorldColliders
{
    readonly TileColliderProvenance[] _provenance;
    internal string MovementWorldId { get; }
    public OrdinaryMovementExtent MovementExtent { get; }
    /// <summary>The certified ground height for the ordinary contact controller. It reads a private copy of the terrain
    /// captured at build, so it is safe for concurrent reads and an endorsed provider for a parallel contact navigation
    /// bake.</summary>
    public Func<float, float, float> MovementGroundHeight { get; }
    /// <summary>The certified ground normal matching <see cref="MovementGroundHeight"/>, with the same capture and
    /// the same endorsement.</summary>
    public Func<float, float, Vector3> MovementGroundNormal { get; }

    static OrdinaryMovementExtent DescribeExtent(TileWorldDocument document, RegionCoord[] regions,
        TileColliderProvenance[] provenance, TileMovementSnapshot ground, TileMediumSampler water)
    {
        Vector3 min = new(0, ground.MinimumY, 0), max = new(0, ground.MaximumY, 0);
        bool first = true;
        foreach (RegionCoord region in regions)
        {
            Vector3 a = TileWorldSpace.ToWorld(region.OriginX, ground.MinimumY, region.OriginZ + TileRegion.Size, document.TileSize);
            Vector3 b = TileWorldSpace.ToWorld(region.OriginX + TileRegion.Size, ground.MaximumY, region.OriginZ, document.TileSize);
            min = first ? a : Vector3.Min(min, a);
            max = first ? b : Vector3.Max(max, b);
            first = false;
        }
        foreach (TileColliderProvenance row in provenance)
        {
            min = Vector3.Min(min, row.AbsoluteMinimum);
            max = Vector3.Max(max, row.AbsoluteMaximum);
        }
        if (water.HasColumns)
        {
            min = Vector3.Min(min, water.Extent.Minimum);
            max = Vector3.Max(max, water.Extent.Maximum);
        }
        return new(min, max, ground.MinimumY, ground.MaximumY);
    }
}
