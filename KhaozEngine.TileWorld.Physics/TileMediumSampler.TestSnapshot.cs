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

    // The legacy computation keeps the same captured geometry and output assembly, with no
    // continuous-bed root certificate. Copies never mutate the producer's captured shapes.
    internal TileWaterTraceTestSnapshot CaptureLegacyTraceForTests() => CaptureTraceForTests() with
    {
        Faces = _field.Faces.Select(face => face with { Shape = face.Shape.WithoutBedSeamForTests() }).ToArray(),
    };
}

internal sealed record TileWaterTraceTestSnapshot(TileWaterColumn[] Columns, TileWaterBoundary[] Boundaries,
    TileWaterBoundary[] Exterior, TileWaterColumn[] Nonuniform, TileWaterPrism[] Prisms,
    TileWaterPrismFace[] Faces, CapturedTileGround Ground, TileWaterColumnIndex PointIndex,
    int[] PrismStarts, int[] FaceStarts);
