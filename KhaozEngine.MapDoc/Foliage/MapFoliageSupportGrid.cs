using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    /// <summary>Whether the shared kernel could select this surface for a sample in an inclusive world-height band:
    /// a Supported face through its exact logical faces, or a LegacyFallback cell through its legacy recipe height.</summary>
    internal bool MeetsBand(MapExactValue low, MapExactValue high)
    {
        if (Cells.Values.Select(c => c.Faces).Distinct().Any(f => f.IntersectsHeight(low, high))) return true;
        if (Surface.Frame != MapLatticeFrame.ImportedMetreCentimetre || Surface.PresencePolicy != MapPresencePolicy.LegacyTileWorld)
            return false;
        foreach (MapFoliageSupportCell cell in Cells.Values)
            if (LegacyMeetsBand(cell.Corners, low, high)) return true;
        return false;
    }

    // A LegacyFallback height is MapLegacyBilinear.Evaluate over the cell's binary32 corner metres hCm * 0.01f, which
    // are exactly these Corners for this frame, then the kernel's Height under an effective transform. With S the
    // largest corner magnitude and u = 2^-24, every binary32 operation returns x(1 + e) + n with |e| <= u, where n is
    // nonzero only for an underflowing product and |n| <= 2^-150. For fx and fz in [0, 1], |d * f| <= |d|, and the
    // exact bilinear value B of the corners lies between their minimum and maximum, so |B| <= S. Each edge lerp
    // h0 + (h1 - h0) * f then errs by at most (4u + 2u^2)(1 + u)S + u S + 2n < 5.001 u S + 2n, since |h1 - h0| <= 2S.
    // The final lerp south + (north - south) * fz adds at most 4.01 u S from its difference and product, plus u times
    // a sum below 1.01 S from its own rounding, so |Evaluate - B| < 10.1 u S + 4n. That is under 11 ulps of S, as
    // u S < ulp(S). Without a transform the recipe may floor a point on a shared edge into the neighbour cell, where
    // the zero fraction leaves only the shared corners in play, so the bound still holds over this cell's corners.
    // The interval widens outward by 16 u S + 2^-40, which covers the rounding. Height rounds Y + local * Scale
    // in binary64 with a positive Scale, freezes to a 2^-20 quantum and rounds to binary32. Each step is monotonic,
    // and both endpoints are dyadic, so ToDouble rounds them once and preserves order. The image of the interval
    // therefore lies between the images of its endpoints. Where an endpoint cannot be mapped, the surface counts.
    bool LegacyMeetsBand(in MapFoliageCellCorners corners, MapExactValue low, MapExactValue high)
    {
        float min = MathF.Min(MathF.Min(corners.Sw, corners.Se), MathF.Min(corners.Nw, corners.Ne));
        float max = MathF.Max(MathF.Max(corners.Sw, corners.Se), MathF.Max(corners.Nw, corners.Ne));
        float largest = MathF.Max(MathF.Abs(min), MathF.Abs(max));
        try
        {
            MapExactValue pad = MapExactValue.FromSingle(largest).Multiply(RelativePad).Add(AbsolutePad);
            MapExactValue first = MapExactValue.FromSingle(min).Subtract(pad), second = MapExactValue.FromSingle(max).Add(pad);
            if (Kernel is { } kernel)
            {
                MapExactValue a = kernel.Height(first), b = kernel.Height(second);
                (first, second) = a.CompareTo(b) <= 0 ? (a, b) : (b, a);
            }
            return first.CompareTo(high) <= 0 && second.CompareTo(low) >= 0;
        }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException or MapDocumentException) { return true; }
    }

    static readonly MapExactValue RelativePad = new(1, 1L << 20);
    static readonly MapExactValue AbsolutePad = new(1, 1L << 40);

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
