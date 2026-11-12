using System.Collections.Immutable;

namespace KhaozEngine.TileWorld.Physics;

// Test-owned immutable copies in evaluator order. The immutable seam is borrowed, never mutated.
internal readonly record struct TileWaterShapeGeometry(
    ImmutableArray<TileWaterConvexShape.Plane> Planes,
    ImmutableArray<TileWaterConvexShape.Basis> Bases,
    TileWaterBedSeam? BedSeam);
