using System;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileMediumSampler : IMovementDryTracePointProof
{
    public bool CanClassifyDryTracePoints(in MovementMediumSweepQuery query, Vector3 physicsOrigin)
    {
        if (!query.IsValid || !float.IsFinite(physicsOrigin.X) || !float.IsFinite(physicsOrigin.Y) ||
            !float.IsFinite(physicsOrigin.Z) || query.Body.CurrentSpace.WorldId != _field.WorldId) return false;
        // A point outside every column's lateral projection returns Known dry before the rounded drawn-bed
        // lookup. Directed bounds enclose exact local + origin, including precision-limit coordinates.
        // Overlapping projections decline even below a bed or above water. Their point lookup can refuse
        // independently of a successful dry prism trace, so geometric dryness is insufficient there.
        var bounds = new TileWaterPathBounds(query, physicsOrigin);
        return _field.PointIndex.Collect(bounds, Span<int>.Empty) == 0;
    }
}
