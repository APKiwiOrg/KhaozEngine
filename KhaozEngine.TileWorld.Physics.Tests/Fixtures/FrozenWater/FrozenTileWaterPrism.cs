using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using I = KhaozEngine.Tests.TileWorld.Physics.Fixtures.FrozenTileWaterArithmetic;
using V = KhaozEngine.Tests.TileWorld.Physics.Fixtures.FrozenTileWaterConvexShape.Vector;
using P = KhaozEngine.Tests.TileWorld.Physics.Fixtures.FrozenTileWaterConvexShape.Plane;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

// Frozen from 418128539 for #1755. Do not synchronize with production optimisations.

/// <summary>Water above one captured drawn triangle, clipped at its body's level surface.</summary>
internal sealed class FrozenTileWaterPrism
{
    internal TileWaterColumn Column { get; }
    internal int ColumnIndex { get; }
    internal DrawnGroundTriangle Triangle { get; }
    internal int TileX { get; }
    internal int TileZ { get; }
    internal float TileSize { get; }
    internal V Reference { get; }
    internal P[] Planes { get; }
    internal FrozenTileWaterConvexShape Shape { get; }
    internal FrozenTileWaterWitness Witness { get; }
    internal double HeightError { get; }
    internal double HorizontalError { get; }
    internal double RepresentationError { get; }

    internal FrozenTileWaterPrism(TileWaterColumn column, DrawnGroundTriangle triangle, int x, int z, float tileSize, int columnIndex)
    {
        Column = column;
        ColumnIndex = columnIndex;
        Triangle = triangle;
        TileX = x;
        TileZ = z;
        TileSize = tileSize;
        Reference = new(I.Point(x) * I.Point(tileSize), I.Point(0), I.Point(-z) * I.Point(tileSize));
        Vector3 a = triangle.A, b = triangle.B, c = triangle.C;
        double bx = (double)b.X - a.X, bz = (double)b.Z - a.Z, cx = (double)c.X - a.X, cz = (double)c.Z - a.Z;
        double determinant = bx * cz - cx * bz;
        // The ideal triangle is enclosed against the shared binary32 evaluator below.
        double by = (double)b.Y - a.Y, cy = (double)c.Y - a.Y;
        double gx = (by * cz - cy * bz) / determinant, gz = (bx * cy - cx * by) / determinant;
        double arithmetic = FrozenTileWaterDrawnGroundRounding.Arithmetic(triangle);
        I exactBx = I.Point(b.X) - I.Point(a.X), exactBz = I.Point(b.Z) - I.Point(a.Z);
        I exactCx = I.Point(c.X) - I.Point(a.X), exactCz = I.Point(c.Z) - I.Point(a.Z);
        I exactBy = I.Point(b.Y) - I.Point(a.Y), exactCy = I.Point(c.Y) - I.Point(a.Y);
        I exactDeterminant = exactBx * exactCz - exactCx * exactBz;
        I exactGx = (exactBy * exactCz - exactCy * exactBz) / exactDeterminant;
        I exactGz = (exactBx * exactCy - exactCx * exactBy) / exactDeterminant;
        double gxError = Math.Max(Math.Abs(exactGx.Low - gx), Math.Abs(exactGx.High - gx));
        double gzError = Math.Max(Math.Abs(exactGz.Low - gz), Math.Abs(exactGz.High - gz));
        I heightError = I.Point(arithmetic) + (I.Point(gxError) + I.Point(gzError)) * I.Point(tileSize) +
            I.Point(Math.Abs(gx)) * I.Point(FrozenTileWaterDrawnGroundRounding.Coordinate(column.Minimum.X, column.Maximum.X, x, tileSize)) +
            I.Point(Math.Abs(gz)) * I.Point(FrozenTileWaterDrawnGroundRounding.Coordinate(column.Minimum.Z, column.Maximum.Z, z, tileSize));
        HeightError = heightError.High;
        double rx = FrozenTileWaterConvexShape.Mid(Reference.X), rz = FrozenTileWaterConvexShape.Mid(Reference.Z);
        HorizontalError = Math.Max(Math.Max(Math.Abs(column.Minimum.X - rx), Math.Abs(column.Maximum.X - (rx + tileSize))),
            Math.Max(Math.Abs(column.Maximum.Z - rz), Math.Abs(column.Minimum.Z - (rz - tileSize))));
        double grade = Math.Sqrt(gx * gx + gz * gz);
        bool cut = Math.Min(a.Y, Math.Min(b.Y, c.Y)) - HeightError <= column.Maximum.Y &&
            Math.Max(a.Y, Math.Max(b.Y, c.Y)) + HeightError >= column.Maximum.Y;
        I certificate = I.Point(HeightError) + (I.Point(HorizontalError).Square() * I.Point(2)).Sqrt();
        if (cut && grade > 0) certificate += I.Point(HeightError) / I.Point(grade);
        RepresentationError = certificate.High;
        I anchor = I.Point(a.Y) - I.Point(gx) * I.Point(a.X) - I.Point(gz) * I.Point(a.Z);
        Planes = [Edge(a, b, c), Edge(b, c, a), Edge(c, a, b),
            new(V.Point(gx, -1, gz), I.Point(0) - anchor, true), P.Point(0, 1, 0, column.Maximum.Y),
            new(V.Point(gx, 0, gz), I.Point(column.Maximum.Y) - anchor, true)];
        Shape = new(Planes);
        Witness = new(this);
    }

    internal FrozenTileWaterPrism(TileWaterPrism source, bool rebuild)
    {
        var built = rebuild ? new FrozenTileWaterPrism(source.Column, source.Triangle, source.TileX,
            source.TileZ, source.TileSize, source.ColumnIndex) : null;
        Column = source.Column;
        ColumnIndex = source.ColumnIndex;
        Triangle = source.Triangle;
        TileX = source.TileX;
        TileZ = source.TileZ;
        TileSize = source.TileSize;
        // Defensive synthetic fixtures can deliberately replace planes or restrict bases.
        // Copy their represented input, then evaluate it with the independent arithmetic.
        Reference = built?.Reference ?? (V)source.Reference;
        Planes = built?.Planes ?? System.Linq.Enumerable.ToArray(System.Linq.Enumerable.Select(source.Planes,
            plane => new P(plane.Normal, plane.Offset, plane.HeightSensitive)));
        Shape = built?.Shape ?? new(source.Shape);
        HeightError = built?.HeightError ?? source.HeightError;
        HorizontalError = built?.HorizontalError ?? source.HorizontalError;
        RepresentationError = built?.RepresentationError ?? source.RepresentationError;
        Witness = built?.Witness ?? new(this);
    }

    static P Edge(Vector3 a, Vector3 b, Vector3 inside)
    {
        double nx = (double)b.Z - a.Z, nz = (double)a.X - b.X;
        if (nx * ((double)inside.X - a.X) + nz * ((double)inside.Z - a.Z) > 0) { nx = -nx; nz = -nz; }
        return new(V.Point(nx, 0, nz), I.Point(nx) * I.Point(a.X) + I.Point(nz) * I.Point(a.Z));
    }

    internal V Start(in MovementMediumSweepQuery query, Vector3 origin) => new V(
        I.Point(query.Body.Centre.X) + I.Point(origin.X), I.Point(query.Body.Centre.Y) + I.Point(origin.Y),
        I.Point(query.Body.Centre.Z) + I.Point(origin.Z)) - Reference;

    internal bool Candidate(in MovementMediumSweepQuery query, Vector3 origin)
    {
        return new FrozenTileWaterPathBounds(query, origin).Meets(Column.Minimum, Column.Maximum);
    }

    internal Vector3 Normal(int plane)
    {
        if (plane == 3) return -Triangle.Normal;
        if (plane == 4) return Vector3.UnitY;
        V n = Planes[plane].Normal;
        return Vector3.Normalize(new((float)n.X.Low, (float)n.Y.Low, (float)n.Z.Low));
    }

    internal string BoundaryId(int plane) => plane switch
    {
        3 => Column.BedBoundaryId,
        4 => Column.SurfaceBoundaryId,
        _ => SideId(plane)
    };

    string SideId(int edge)
    {
        (Vector3 a, Vector3 b) = edge switch { 0 => (Triangle.A, Triangle.B), 1 => (Triangle.B, Triangle.C), _ => (Triangle.C, Triangle.A) };
        if (a.X == b.X && a.X == 0) return Column.MinXBoundaryId;
        if (a.X == b.X && a.X == TileSize) return Column.MaxXBoundaryId;
        if (a.Z == b.Z && a.Z == -TileSize) return Column.MinZBoundaryId;
        if (a.Z == b.Z && a.Z == 0) return Column.MaxZBoundaryId;
        return Column.BedBoundaryId + "/joined-triangle";
    }
}
