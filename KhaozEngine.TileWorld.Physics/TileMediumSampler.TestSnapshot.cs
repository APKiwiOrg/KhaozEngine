using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

public sealed partial class TileMediumSampler
{
    // Test-owned synthetic topology reaches the ordinary validation/dispatch/memo boundary too.
    // The captured ground and indexes are immutable borrowed values. This factory is never used by a producer.
    internal static TileMediumSampler FromTraceForTests(TileWaterTraceTestSnapshot geometry, string worldId) =>
        new(geometry, worldId);

    TileMediumSampler(TileWaterTraceTestSnapshot geometry, string worldId)
    {
        _tileSize = 1;
        _field = new(geometry, worldId);
        MediumDelegate = MediumAt;
    }

    sealed partial class FieldSnapshot
    {
        internal FieldSnapshot(TileWaterTraceTestSnapshot geometry, string worldId)
        {
            WorldId = worldId;
            SemanticIdentity = "test-captured-water";
            Columns = geometry.Columns.Concat(geometry.Nonuniform).ToArray();
            Ground = geometry.Ground;
            Prisms = geometry.Prisms;
            Faces = geometry.Faces;
            PrismStarts = geometry.PrismStarts;
            FaceStarts = geometry.FaceStarts;
            FlatColumns = geometry.Columns;
            NonuniformColumns = geometry.Nonuniform;
            FlatBoundaries = geometry.Boundaries;
            FlatExterior = geometry.Exterior;
            PointIndex = geometry.PointIndex;
            NonuniformIndex = new(NonuniformColumns);
            FlatIndex = new(FlatColumns, FlatBoundaries, FlatExterior);
            Vector3 minimum = Vector3.Zero, maximum = Vector3.Zero;
            for (int i = 0; i < Columns.Length; i++)
            {
                minimum = i == 0 ? Columns[i].Minimum : Vector3.Min(minimum, Columns[i].Minimum);
                maximum = i == 0 ? Columns[i].Maximum : Vector3.Max(maximum, Columns[i].Maximum);
            }
            Extent = new MovementWaterExtent(minimum, maximum);
        }
    }

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
