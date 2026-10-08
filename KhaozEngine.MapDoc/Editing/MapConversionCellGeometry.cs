using System;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Editing;

/// <summary>A cell-scoped view of the compiler's native planes and paint, including accepted legacy faces.</summary>
internal sealed class MapConversionCellGeometry
{
    internal MapSurfaceRef Surface { get; }
    internal MapSurfacePatch Patch { get; }
    internal long CellX { get; }
    internal long CellZ { get; }
    internal int X { get; }
    internal int Z { get; }
    internal int SlotCell => (Patch.CellMinZ + Z) * 64 + Patch.CellMinX + X;
    internal MapSurfaceCell Cell => Patch.Cells[Z * Patch.Width + X];
    internal MapCompiledPatch Physical { get; }

    internal MapConversionCellGeometry(MapSurfaceRef surface, MapSurfacePatch patch, long cellX, long cellZ)
    {
        MapSurfaceCompiler.Validate(surface, patch);
        Surface = surface;
        Patch = patch;
        CellX = cellX;
        CellZ = cellZ;
        MapLatticeAddress origin = patch.CornerAddress(0, 0);
        long x = checked(cellX - origin.X), z = checked(cellZ - origin.Z);
        if (x < 0 || x >= patch.Width || z < 0 || z >= patch.Depth || !patch.IsPresent((int)x, (int)z))
            throw new MapDocumentException($"conversion requires present cell ({cellX}, {cellZ})");
        X = (int)x;
        Z = (int)z;
        MapSurfacePatch physical = patch;
        if (surface.PresencePolicy == MapPresencePolicy.LegacyTileWorld)
        {
            physical = patch.Clone();
            for (int i = 0; i < physical.Cells.Length; i++)
                physical.Cells[i] = physical.Cells[i] with { Flags = physical.Cells[i].Flags & ~MapCellFlags.NoDraw };
        }
        Physical = MapSurfaceCompiler.Compile(surface with { PresencePolicy = MapPresencePolicy.Native },
            physical, MapSlotCellMask.Of(new[] { SlotCell }), MapSurfaceCompiler.MaxFacesPerPatch);
    }

    internal MapExactXz World(MapExactValue x, MapExactValue z)
    {
        MapExactValue unit = Surface.Frame.CellUnitMetres.Exact();
        MapExactValue worldZ = new MapExactValue(CellZ, 1).Add(z).Multiply(unit);
        return new(new MapExactValue(CellX, 1).Add(x).Multiply(unit),
            Surface.Frame.RowDirection == MapRowDirection.NegativeZ ? worldZ.Negate() : worldZ);
    }

    internal MapExactValue Height(MapExactValue x, MapExactValue z)
    {
        MapExactXz world = World(x, z);
        foreach (MapCompiledFace face in Physical.Faces)
            if (MapSubdividedTriangle.Height(Physical.ExactTriangle(face), world.X, world.Z) is { } height)
                return height;
        throw new MapDocumentException("canonical conversion plane is not representable");
    }

    internal bool Overlay(MapExactValue x, MapExactValue z)
    {
        MapExactXz world = World(x, z);
        for (int i = 0; i < Physical.Faces.Count; i++)
            if (MapSubdividedTriangle.Height(Physical.ExactTriangle(Physical.Faces[i]), world.X, world.Z) is not null)
                return Physical.Paint[i].OverlayCovers;
        throw new MapDocumentException("canonical conversion paint is not representable");
    }

    internal int FineHeight(int x, int z, int k)
    {
        MapExactValue value = Height(new(x, k), new(z, k)).Divide(Surface.Frame.HeightUnitMetres.Exact())
            .Multiply(new(k, 1));
        if (value.Denominator != 1) throw new MapDocumentException("fine corner height is not representable");
        if (value.Numerator < int.MinValue || value.Numerator > int.MaxValue)
            throw new MapDocumentException("fine corner height overflow");
        return (int)value.Numerator;
    }

    internal bool Coplanar()
    {
        MapExactTriangle first = Physical.ExactTriangle(Physical.Faces[0]);
        MapExactPoint a = first.A, b = first.B, c = first.C;
        MapExactValue nx = b.Y.Subtract(a.Y).Multiply(c.Z.Subtract(a.Z))
            .Subtract(b.Z.Subtract(a.Z).Multiply(c.Y.Subtract(a.Y)));
        MapExactValue ny = b.Z.Subtract(a.Z).Multiply(c.X.Subtract(a.X))
            .Subtract(b.X.Subtract(a.X).Multiply(c.Z.Subtract(a.Z)));
        MapExactValue nz = b.X.Subtract(a.X).Multiply(c.Y.Subtract(a.Y))
            .Subtract(b.Y.Subtract(a.Y).Multiply(c.X.Subtract(a.X)));
        foreach (MapExactPoint p in Physical.ExactVertices)
            if (nx.Multiply(p.X.Subtract(a.X)).Add(ny.Multiply(p.Y.Subtract(a.Y)))
                .Add(nz.Multiply(p.Z.Subtract(a.Z))).Sign != 0) return false;
        return true;
    }

    internal bool SplitSwNe() => MapSurfaceTopology.SplitSwNe(Patch.Height(X, Z), Patch.Height(X + 1, Z),
        Patch.Height(X, Z + 1), Patch.Height(X + 1, Z + 1), Cell.Cut, Cell.Rotation, Cell.Topology);

    internal MapSurfaceCell FineCell(int x, int z, int k, MapCellConversionClass classification)
    {
        MapSurfaceCell cell = Cell with
        {
            Cut = MapOverlayCut.Full,
            Rotation = 0,
            Flags = Cell.Flags & ~MapCellFlags.NoDraw,
            Topology = MapCellTopology.Auto
        };
        if (classification == MapCellConversionClass.UnsupportedEncoding)
            return cell with { Overlay = Overlay(new(2 * x + 1, 2 * k), new(2 * z + 1, 2 * k)) ? Cell.Overlay : (ushort)0 };

        bool? diagonal = null;
        if (classification == MapCellConversionClass.ExactDiagonal || classification == MapCellConversionClass.NotRepresentable)
        {
            bool split = SplitSwNe();
            if (split ? x == z : x + z == k - 1) diagonal = split;
        }
        if (Cell.Overlay != 0)
        {
            if (Cell.Cut == MapOverlayCut.DiagonalHalf && (Cell.Rotation % 2 == 0 ? x == z : x + z == k - 1))
                diagonal = Cell.Rotation % 2 == 0;
            else if (Cell.Cut is MapOverlayCut.CornerQuarter or MapOverlayCut.CornerThreeQuarter)
            {
                int dx = Cell.Rotation is 2 or 3 ? k - 1 - x : x;
                int dz = Cell.Rotation is 1 or 2 ? k - 1 - z : z;
                if (2 * (dx + dz + 1) == k) diagonal = Cell.Rotation % 2 != 0;
            }
        }
        if (diagonal is not { } swne)
            return cell with { Overlay = Overlay(new(2 * x + 1, 2 * k), new(2 * z + 1, 2 * k)) ? Cell.Overlay : (ushort)0 };
        cell = cell with { Topology = swne ? MapCellTopology.ForceSwNe : MapCellTopology.ForceNwSe };
        Span<MapLatticeTriangle> triangles = stackalloc MapLatticeTriangle[4];
        _ = MapSurfaceTopology.Triangulate(MapOverlayCut.Full, 0, swne, triangles);
        bool first = Paint(triangles[0]), second = Paint(triangles[1]);
        if (first == second) return cell with { Overlay = first ? Cell.Overlay : (ushort)0 };
        return cell with { Cut = MapOverlayCut.DiagonalHalf, Rotation = (byte)(swne ? first ? 2 : 0 : first ? 3 : 1) };

        bool Paint(MapLatticeTriangle triangle)
        {
            (int ax, int az) = MapSurfaceTopology.LocalTwice(triangle.A);
            (int bx, int bz) = MapSurfaceTopology.LocalTwice(triangle.B);
            (int cx, int cz) = MapSurfaceTopology.LocalTwice(triangle.C);
            return Overlay(new(6 * x + ax + bx + cx, 6 * k), new(6 * z + az + bz + cz, 6 * k));
        }
    }
}
