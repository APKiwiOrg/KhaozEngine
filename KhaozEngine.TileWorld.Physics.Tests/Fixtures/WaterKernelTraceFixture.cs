using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Deliberately synthetic topology reaches bounded-storage and refusal-order paths without a large world.
internal static class WaterKernelTraceFixture
{
    internal static CapturedTileGround Ground(bool hole = false)
    {
        var document = new TileWorldDocument { Id = "water-kernel-matrix" };
        document.GetOrCreateRegion(new(0, 0));
        for (int z = 0; z < 2; z++)
            for (int x = 0; x < 17; x++) document.SetUnderlay(x, z, 0, 1);
        for (int z = 0; z <= 2; z++)
            for (int x = 0; x <= 17; x++) document.SetCornerHeightCm(x, z, 0, -100);
        if (hole) document.SetUnderlay(1, 0, 0, 0);
        return TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), "water-world")
            .Medium.CaptureTraceForTests().Ground;
    }

    internal static TileWaterColumn Column(int ordinal = 0, float x = 0, float size = 1, float surface = -0.37f) =>
        new(new("water-world", "matrix/" + ordinal), new(x, -1, -size), new(x + size, surface, 0),
            "min-x", "max-x", "min-z", "max-z", "bed", "surface");

    internal static TileWaterPrism Piece(int x = 0, int column = 0, bool second = false, float size = 1,
        float minimumInset = 0)
    {
        var water = Column(column, x * size, size);
        if (minimumInset != 0) water = water with { Minimum = water.Minimum + Vector3.UnitX * minimumInset };
        var triangle = second
            ? new DrawnGroundTriangle(TileLatticePoint.Sw, TileLatticePoint.Ne, TileLatticePoint.Nw,
                new(0, -1, 0), new(size, -1, -size), new(0, -1, -size))
            : new DrawnGroundTriangle(TileLatticePoint.Sw, TileLatticePoint.Se, TileLatticePoint.Ne,
                new(0, -1, 0), new(size, -1, 0), new(size, -1, -size));
        return new(water, triangle, x, 0, size, column);
    }

    internal static TileWaterTraceTestSnapshot Snapshot(TileWaterColumn[] columns, TileWaterPrism[] prisms,
        TileWaterPrismFace[] faces, CapturedTileGround ground, int[]? prismStarts = null, int[]? faceStarts = null) =>
        new(columns, [], [], columns, prisms, faces, ground, new(columns),
            prismStarts ?? [0, prisms.Length], faceStarts ?? [0, faces.Length]);

    internal static MovementMediumSweepQuery Query(float x = 0.75f) =>
        new(new(new(x, -0.5f, -0.25f), 0.25f, 0.75f, MovementWaterFieldTests.Space, null), Vector3.Zero);

    internal static TileWaterTraceTestSnapshot RepeatedPieces(int count, bool candidates = false, bool unresolved = false)
    {
        var first = Piece();
        if (unresolved)
            first.Planes[3] = new(TileWaterConvexShape.Vector.Point(double.NaN, -1, 0), TileWaterArithmetic.Point(0), true);
        var pieces = Enumerable.Repeat(first, count).ToArray();
        var columns = candidates ? Enumerable.Range(0, count).Select(i => Column(i)).ToArray() : [first.Column];
        return Snapshot(columns, pieces, [], Ground(), candidates ? Enumerable.Range(0, count + 1).ToArray() : null,
            candidates ? new int[count + 1] : null);
    }

    internal static TileWaterTraceTestSnapshot RepeatedFaces(int count, bool wet)
    {
        var piece = Piece();
        // Both false is a defensive storage fixture, never produced by face construction.
        var faces = Enumerable.Range(0, count).Select(_ => new TileWaterPrismFace(0, 4, wet, false,
            count >= 2048 ? new TileWaterConvexShape(piece.Planes.ToArray()) : piece.Shape)).ToArray();
        return Snapshot([piece.Column], [piece], faces, Ground());
    }

    internal static TileWaterTraceTestSnapshot SelectedMisses(int count)
    {
        var column = Column(size: 16);
        var triangle = new DrawnGroundTriangle(TileLatticePoint.Sw, TileLatticePoint.Se, TileLatticePoint.Ne,
            new(10, -1, 0), new(11, -1, 0), new(11, -1, -1));
        var piece = new TileWaterPrism(column, triangle, 0, 0, 16, 0);
        return Snapshot([column], Enumerable.Repeat(piece, count).ToArray(), [], Ground());
    }

    internal static TileWaterTraceTestSnapshot WitnessPressure(int goodContacts)
    {
        var good = Piece();
        var bad = Piece(1, 1, second: true, minimumInset: 0.0001f);
        var faces = Enumerable.Repeat(new TileWaterPrismFace(0, 4, true, false, good.Shape), goodContacts)
            .Append(new(1, 4, true, false, bad.Shape)).ToArray();
        return Snapshot([good.Column, bad.Column], [good, bad], faces, Ground(hole: true),
            [0, 1, 2], [0, goodContacts, goodContacts + 1]);
    }

    internal static TileWaterTraceTestSnapshot SpanPressure(bool oneMore)
    {
        var piece = Piece(second: true);
        var faces = Enumerable.Range(0, 31).Select(i => new TileWaterPrismFace(0, 4, false, false,
            new([TileWaterConvexShape.Plane.Point(1, 0, 0, 0.07123456789 + i * 0.01111111111)]))).ToList();
        faces.Add(new(0, 4, false, false, new([TileWaterConvexShape.Plane.Point(1, 0, 0, 0.25)])));
        if (oneMore) faces.Add(new(0, 4, false, false, new([TileWaterConvexShape.Plane.Point(1, 0, 0, 0.125)])));
        return Snapshot([piece.Column], [piece], faces.ToArray(), Ground());
    }

    internal static TileWaterTraceTestSnapshot RealFaceTypes()
    {
        var first = Piece();
        var second = Piece(1, 1, second: true);
        second = new(second.Column with { Maximum = second.Column.Maximum with { Y = -0.6f } },
            second.Triangle, second.TileX, second.TileZ, second.TileSize, second.ColumnIndex);
        var pieces = new[] { first, second };
        var faces = TileWaterPrismFaces.Build(pieces).OrderBy(face => pieces[face.Prism].ColumnIndex).ToArray();
        int firstFaces = faces.Count(face => face.Prism == 0);
        return Snapshot([first.Column, second.Column], pieces, faces, Ground(), [0, 1, 2], [0, firstFaces, faces.Length]);
    }
}
