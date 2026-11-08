using System.Linq;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileMediumSampler
{
    // Copies of mutable arrays for the frozen test-only trace reference. Ground and the point index
    // are immutable captured geometry. They are borrowed, never mutated or disposed by tests.
    internal TileWaterTraceTestSnapshot CaptureTraceForTests() => new(_field.FlatColumns.ToArray(),
        _field.FlatBoundaries.ToArray(), _field.FlatExterior.ToArray(), _field.NonuniformColumns.ToArray(),
        _field.Prisms.ToArray(), _field.Faces.ToArray(), _field.Ground, _field.PointIndex,
        _field.PrismStarts.ToArray(), _field.FaceStarts.ToArray());
}

internal sealed record TileWaterTraceTestSnapshot(TileWaterColumn[] Columns, TileWaterBoundary[] Boundaries,
    TileWaterBoundary[] Exterior, TileWaterColumn[] Nonuniform, TileWaterPrism[] Prisms,
    TileWaterPrismFace[] Faces, CapturedTileGround Ground, TileWaterColumnIndex PointIndex,
    int[] PrismStarts, int[] FaceStarts);
