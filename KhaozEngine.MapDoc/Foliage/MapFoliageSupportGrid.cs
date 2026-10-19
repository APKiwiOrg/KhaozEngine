using System;
using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.MapDoc.Foliage;

/// <summary>One support surface's prepared cells within a chunk, keyed by global lattice cell.</summary>
internal sealed class MapFoliageSupportGrid(MapSurfaceRef surface, MapResourceTransform? kernel)
{
    readonly float _cell = surface.Frame.CellUnitMetres.Exact().ToSingle();
    internal MapSurfaceRef Surface { get; } = surface;
    internal MapResourceTransform? Kernel { get; } = kernel;
    internal Dictionary<(long X, long Z), MapFoliageSupportCell> Cells { get; } = new();

    /// <summary>The lattice cell holding a world XZ and the local fractions inside it, in the released float operations:
    /// tile X is world X over the cell size, tile Z negates world Z first on a NegativeZ frame.</summary>
    internal (long X, long Z, float LocalX, float LocalZ) Locate(float worldX, float worldZ)
    {
        (float tx, float tz) = Tile(worldX, worldZ);
        long x = (long)MathF.Floor(tx), z = (long)MathF.Floor(tz);
        return (x, z, tx - x, tz - z);
    }

    /// <summary>The local fractions of a world XZ inside a given cell, in the same released float operations.</summary>
    internal (float LocalX, float LocalZ) LocalIn(float worldX, float worldZ, long x, long z)
    {
        (float tx, float tz) = Tile(worldX, worldZ);
        return (tx - x, tz - z);
    }

    (float X, float Z) Tile(float worldX, float worldZ)
    {
        float tx, tz;
        bool negative = Surface.Frame.RowDirection == MapRowDirection.NegativeZ;
        if (Kernel is null)
        {
            tx = worldX / _cell;
            tz = negative ? -worldZ / _cell : worldZ / _cell;
        }
        else
        {
            MapEffectiveTransform pose = Kernel.Transform;
            (double s, double c) = Math.SinCos(pose.YawRadians);
            double dx = worldX - pose.X, dz = worldZ - pose.Z;
            double lx = (c * dx - s * dz) / pose.Scale, lz = (s * dx + c * dz) / pose.Scale;
            tx = (float)(lx / _cell);
            tz = (float)((negative ? -lz : lz) / _cell);
        }
        return (tx, tz);
    }
}

/// <summary>One prepared support cell: its authored cell, released corners and split, its logical faces, and for a Surface
/// binding its support owner. Occupied space and every refusal are decided per candidate.</summary>
internal sealed class MapFoliageSupportCell
{
    internal required MapPatchKey Key { get; init; }
    internal required int SlotCell { get; init; }
    internal required MapSurfaceCell Cell { get; init; }
    internal required bool Split { get; init; }
    internal required bool Fallback { get; init; }
    internal required MapFoliageCellCorners Corners { get; init; }
    internal required MapFoliagePatchFaces Faces { get; init; }
    internal required MapResourceTransform? Kernel { get; init; }
    internal bool Selected { get; private set; }
    internal string? OwnerId { get; private set; }

    internal void Select(string? owner)
    {
        Selected = owner is not null;
        OwnerId = owner;
    }
}

/// <summary>A patch's logical faces in effective coordinates with their world-painted coverage, indexed by cell and
/// parent triangle.</summary>
internal sealed class MapFoliagePatchFaces
{
    readonly MapCompiledPatch _compiled;
    readonly Dictionary<(int Primitive, byte Parent), List<int>> _index = new();

    internal MapFoliagePatchFaces(MapCompiledPatch compiled)
    {
        _compiled = compiled;
        for (int i = 0; i < compiled.Faces.Count; i++)
        {
            MapFaceKey key = compiled.Faces[i].Key;
            if (!_index.TryGetValue((key.Primitive, key.ParentTriangle), out List<int>? list)) _index.Add((key.Primitive, key.ParentTriangle), list = new());
            list.Add(i);
        }
    }

    internal int Count => _compiled.Faces.Count;

    /// <summary>Whether any prepared logical triangle can intersect an inclusive world-height search band. Logical
    /// faces include legacy fallback cells, and the compiled effective coordinates already carry their transform.</summary>
    internal bool IntersectsHeight(MapExactValue low, MapExactValue high)
    {
        foreach (MapCompiledFace face in _compiled.Faces)
        {
            MapExactTriangle triangle = _compiled.ExactTriangle(face);
            if ((triangle.A.Y.CompareTo(low) >= 0 || triangle.B.Y.CompareTo(low) >= 0 || triangle.C.Y.CompareTo(low) >= 0) &&
                (triangle.A.Y.CompareTo(high) <= 0 || triangle.B.Y.CompareTo(high) <= 0 || triangle.C.Y.CompareTo(high) <= 0))
                return true;
        }
        return false;
    }

    /// <summary>The canonical face of a parent triangle at a world point. An unsubdivided parent is its one face. A
    /// subdivided parent picks its first child holding the point exactly.</summary>
    internal (MapFaceKey Face, MapPaintCoverage Paint) Face(int slotCell, byte parent, MapExactXz point)
    {
        List<int> faces = _index[(slotCell, parent)];
        int chosen = faces[0];
        if (faces.Count > 1)
            foreach (int index in faces)
                if (MapSubdividedTriangle.Height(_compiled.ExactTriangle(_compiled.Faces[index]), point.X, point.Z) is not null)
                {
                    chosen = index;
                    break;
                }
        return (_compiled.Faces[chosen].Key, _compiled.Paint[chosen]);
    }

    internal static string Describe(MapPatchKey key) =>
        string.Create(CultureInfo.InvariantCulture, $"{key.SurfaceId}|{key.SlotX}|{key.SlotZ}");
}
