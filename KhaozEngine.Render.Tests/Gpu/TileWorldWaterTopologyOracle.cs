using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Render3D;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Render3D;

namespace KhaozEngine.Tests.Gpu;

/// <summary>One world-space bed triangle lifted to binary64, with its position in region then emission order.</summary>
internal readonly record struct TileWaterWorldTriangle(Vector3 A, Vector3 B, Vector3 C, double OffsetX, double OffsetZ,
    int Ordinal)
{
    internal (double X, double Y, double Z) World(int corner)
    {
        Vector3 p = corner == 0 ? A : corner == 1 ? B : C;
        return (p.X + OffsetX, p.Y, p.Z + OffsetZ);
    }
}

/// <summary>A water rectangle in absolute world XZ, closed.</summary>
internal readonly record struct TileWaterWorldRect(double MinX, double MinZ, double MaxX, double MaxZ)
{
    internal static TileWaterWorldRect Of(in WaterPlane plane) => new((double)plane.CenterX - plane.HalfExtentX,
        (double)plane.CenterZ - plane.HalfExtentZ, (double)plane.CenterX + plane.HalfExtentX,
        (double)plane.CenterZ + plane.HalfExtentZ);

    internal bool Contains(double x, double z) => x >= MinX && x <= MaxX && z >= MinZ && z <= MaxZ;
    internal TileWaterWorldRect Shifted(double dx) => new(MinX + dx, MinZ, MaxX + dx, MaxZ);
}

/// <summary>An independently built triangle field and the binding origin it was built against.</summary>
internal sealed record TileWaterIndependentField(WaterOpticsTriangleField Field, Vector3 Origin, int Attempts);

/// <summary>The CPU oracle for drawn TileWorld beds. Vertical-line intersection in binary64 over the exact
/// mesh instances the view submitted, plus witness topologies and an independent field construction. It shares
/// no code with the TileWorld producer or the scene sampler.</summary>
internal static class TileWorldWaterTopologyOracle
{
    /// <summary>Drawn triangles ordered by region row, then column, then emitted index order. Draws whose
    /// projected area is exactly zero are skipped, as no single-valued bed can use them.</summary>
    internal static List<TileWaterWorldTriangle> Drawn(IEnumerable<(GltfMesh Mesh, Matrix4x4 World)> draws, float tileSize)
    {
        var ordered = draws.Select(d => (d.Mesh, d.World,
                Row: Math.Round(-d.World.M43 / (64.0 * tileSize)), Column: Math.Round(d.World.M41 / (64.0 * tileSize))))
            .OrderBy(d => d.Row).ThenBy(d => d.Column).ToArray();
        var result = new List<TileWaterWorldTriangle>();
        foreach (var draw in ordered)
        {
            if (draw.World.M11 != 1 || draw.World.M22 != 1 || draw.World.M33 != 1 || draw.World.M12 != 0
                || draw.World.M13 != 0 || draw.World.M21 != 0 || draw.World.M23 != 0 || draw.World.M31 != 0
                || draw.World.M32 != 0 || draw.World.M42 != 0)
                throw new InvalidOperationException("TileWorld ground is drawn with a pure XZ translation.");
            ModelVertex[] v = draw.Mesh.Vertices;
            uint[] index = draw.Mesh.Indices32;
            for (int i = 0; i + 2 < index.Length; i += 3)
            {
                var t = new TileWaterWorldTriangle(v[index[i]].Position, v[index[i + 1]].Position, v[index[i + 2]].Position,
                    draw.World.M41, draw.World.M43, result.Count);
                if (ProjectedArea(t) != 0) result.Add(t);
            }
        }
        return result;
    }

    /// <summary>Two triangles per drawable tile over a tile rectangle, split by the supplied rule. Heights are the
    /// authored corner heights, so this is a surface the document could imply but the mesher did not draw.</summary>
    internal static List<TileWaterWorldTriangle> Split(TileWorldDocument doc, int x0, int z0, int x1, int z1,
        Func<short, short, short, short, bool> splitSwNe)
    {
        var result = new List<TileWaterWorldTriangle>();
        float s = doc.TileSize;
        for (int z = z0; z < z1; z++)
            for (int x = x0; x < x1; x++)
            {
                if (doc.GetUnderlay(x, z, 0) == 0) continue;
                short h00 = doc.CornerHeightCm(x, z, 0), h10 = doc.CornerHeightCm(x + 1, z, 0);
                short h01 = doc.CornerHeightCm(x, z + 1, 0), h11 = doc.CornerHeightCm(x + 1, z + 1, 0);
                var sw = new Vector3(x * s, h00 / 100f, -z * s);
                var se = new Vector3((x + 1) * s, h10 / 100f, -z * s);
                var nw = new Vector3(x * s, h01 / 100f, -(z + 1) * s);
                var ne = new Vector3((x + 1) * s, h11 / 100f, -(z + 1) * s);
                if (splitSwNe(h00, h10, h01, h11))
                {
                    result.Add(new(sw, se, ne, 0, 0, result.Count));
                    result.Add(new(sw, ne, nw, 0, 0, result.Count));
                }
                else
                {
                    result.Add(new(sw, se, nw, 0, 0, result.Count));
                    result.Add(new(se, ne, nw, 0, 0, result.Count));
                }
            }
        return result;
    }

    /// <summary>The Full-detail mesh of each region, the surface a coarse region would have drawn at Full LOD.</summary>
    internal static List<TileWaterWorldTriangle> FullDetail(TileWorldDocument doc, TileWorldCatalogs catalogs,
        IEnumerable<RegionCoord> regions)
    {
        var draws = new List<(GltfMesh, Matrix4x4)>();
        foreach (RegionCoord region in regions)
            if (TileGroundMesher.Build(doc, catalogs, region, 0, TileGroundLod.Full) is { } mesh)
                draws.Add((mesh, TileGroundMesher.WorldMatrix(doc, region)));
        return Drawn(draws, doc.TileSize);
    }

    /// <summary>The bed height under a world XZ point. Every containing triangle must agree, since a drawn
    /// single-valued bed cannot carry two heights at one point.</summary>
    internal static double Height(IReadOnlyList<TileWaterWorldTriangle> triangles, double x, double z)
    {
        double? found = null;
        foreach (TileWaterWorldTriangle t in triangles)
        {
            if (!TryHeight(t, x, z, out double height)) continue;
            if (found is { } previous && Math.Abs(previous - height) > 1e-6)
                throw new InvalidOperationException($"The drawn bed is not single valued at ({x:R}, {z:R}).");
            found ??= height;
        }
        return found ?? throw new InvalidOperationException($"No drawn triangle lies under ({x:R}, {z:R}).");
    }

    static bool TryHeight(in TileWaterWorldTriangle t, double x, double z, out double height)
    {
        (double ax, double ay, double az) = t.World(0);
        (double bx, double by, double bz) = t.World(1);
        (double cx, double cy, double cz) = t.World(2);
        double d = (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
        double l1 = ((x - ax) * (cz - az) - (z - az) * (cx - ax)) / d;
        double l2 = ((bx - ax) * (z - az) - (bz - az) * (x - ax)) / d;
        double l0 = 1 - l1 - l2;
        height = l0 * ay + l1 * by + l2 * cy;
        return l0 >= -1e-9 && l1 >= -1e-9 && l2 >= -1e-9;
    }

    static double ProjectedArea(in TileWaterWorldTriangle t)
    {
        (double ax, _, double az) = t.World(0);
        (double bx, _, double bz) = t.World(1);
        (double cx, _, double cz) = t.World(2);
        return (bx - ax) * (cz - az) - (bz - az) * (cx - ax);
    }

    /// <summary>A field built here from the given world triangles. The binding origin is the whole-metre floor of
    /// the selected vertices, rest depth is reference minus height and certificates are the water rectangles grown
    /// by the TileWorld positional budget. Bin axes double from one per tile until the packing fits.</summary>
    internal static TileWaterIndependentField Field(IReadOnlyList<TileWaterWorldTriangle> source,
        IReadOnlyList<TileWaterWorldRect> rects, float reference, float tileSize)
    {
        double reach = tileSize, budget = Math.Min(0.0001, tileSize / 1024.0);
        var selected = source.Where(t => rects.Any(r => Overlaps(t, r, reach))).ToArray();
        if (selected.Length == 0) throw new InvalidOperationException("No source triangle supports the water.");
        double originX = Math.Floor(selected.Min(t => Math.Min(t.World(0).X, Math.Min(t.World(1).X, t.World(2).X))));
        double originZ = Math.Floor(selected.Min(t => Math.Min(t.World(0).Z, Math.Min(t.World(1).Z, t.World(2).Z))));
        var triangles = new WaterOpticsTriangle[selected.Length];
        float sizeX = 0, sizeZ = 0;
        for (int i = 0; i < selected.Length; i++)
        {
            var local = new Vector3[3];
            for (int corner = 0; corner < 3; corner++)
            {
                (double x, double y, double z) = selected[i].World(corner);
                local[corner] = new Vector3((float)(x - originX), (float)(reference - y), (float)(z - originZ));
                sizeX = MathF.Max(sizeX, local[corner].X);
                sizeZ = MathF.Max(sizeZ, local[corner].Z);
            }
            triangles[i] = new WaterOpticsTriangle(local[0], local[1], local[2]);
        }
        var certificates = rects.Select(r => new WaterOpticsRectangle(
            new Vector2(Down(r.MinX - budget - originX), Down(r.MinZ - budget - originZ)),
            new Vector2(Up(r.MaxX + budget - originX), Up(r.MaxZ + budget - originZ)))).ToArray();
        int columns = (int)Math.Ceiling(sizeX / tileSize), rows = (int)Math.Ceiling(sizeZ / tileSize);
        for (int k = 0; k <= 10; k++)
        {
            try
            {
                var field = new WaterOpticsTriangleField(new Vector2(sizeX, sizeZ), Math.Min(1024, columns << k),
                    Math.Min(1024, rows << k), triangles, certificates);
                return new TileWaterIndependentField(field, new Vector3((float)originX, reference, (float)originZ), k + 1);
            }
            catch (WaterOpticsSurfaceException error) when (error.Reason == WaterOpticsSurfaceFailure.CapacityExceeded) { }
        }
        throw new InvalidOperationException("The independent field exceeded every packing attempt.");
    }

    static bool Overlaps(in TileWaterWorldTriangle t, in TileWaterWorldRect r, double reach)
    {
        double minX = Math.Min(t.World(0).X, Math.Min(t.World(1).X, t.World(2).X));
        double maxX = Math.Max(t.World(0).X, Math.Max(t.World(1).X, t.World(2).X));
        double minZ = Math.Min(t.World(0).Z, Math.Min(t.World(1).Z, t.World(2).Z));
        double maxZ = Math.Max(t.World(0).Z, Math.Max(t.World(1).Z, t.World(2).Z));
        return maxX >= r.MinX - reach && minX <= r.MaxX + reach && maxZ >= r.MinZ - reach && minZ <= r.MaxZ + reach;
    }

    static float Down(double value)
    {
        float f = (float)value;
        return f > value ? MathF.BitDecrement(f) : f;
    }

    static float Up(double value)
    {
        float f = (float)value;
        return f < value ? MathF.BitIncrement(f) : f;
    }

    /// <summary>Decodes the packed triangle records of a field: local x, local z and rest depth per vertex.</summary>
    internal static Vector3[][] PackedTriangles(WaterOpticsTriangleField field)
    {
        ReadOnlySpan<float> atlas = field.AtlasScalars;
        int count = (int)atlas[6], first = (int)atlas[4];
        var result = new Vector3[count][];
        for (int i = 0; i < count; i++)
        {
            int at = first + 12 * i;
            result[i] = [new(atlas[at], atlas[at + 2], atlas[at + 1]), new(atlas[at + 3], atlas[at + 5], atlas[at + 4]),
                new(atlas[at + 6], atlas[at + 8], atlas[at + 7])];
        }
        return result;
    }

    /// <summary>Checks that a producer field holds exactly drawn triangles, as an order-preserving subsequence of
    /// region then emission order, and that every drawn triangle centred inside the water is present. Returns
    /// the number of drawn triangles matched, or throws with the first violation.</summary>
    internal static int VerifySourceOrder(WaterOpticsTriangleField field, Vector3 origin,
        IReadOnlyList<TileWaterWorldTriangle> drawn, IReadOnlyList<TileWaterWorldRect> rects)
    {
        Vector3[][] packed = PackedTriangles(field);
        var matched = new bool[drawn.Count];
        int next = 0;
        for (int i = 0; i < drawn.Count && next < packed.Length; i++)
        {
            bool same = true;
            for (int corner = 0; corner < 3 && same; corner++)
            {
                (double x, double y, double z) = drawn[i].World(corner);
                var local = new Vector3((float)(x - origin.X), (float)(origin.Y - y), (float)(z - origin.Z));
                same = local == packed[next][corner];
            }
            if (!same) continue;
            matched[i] = true;
            next++;
        }
        if (next != packed.Length)
            throw new InvalidOperationException($"Packed triangle {next} of {packed.Length} is not the next drawn triangle in order.");
        for (int i = 0; i < drawn.Count; i++)
        {
            double cx = (drawn[i].World(0).X + drawn[i].World(1).X + drawn[i].World(2).X) / 3;
            double cz = (drawn[i].World(0).Z + drawn[i].World(1).Z + drawn[i].World(2).Z) / 3;
            if (!matched[i] && rects.Any(r => r.Contains(cx, cz)))
                throw new InvalidOperationException($"Drawn triangle {i} centred at ({cx:R}, {cz:R}) inside the water is not packed.");
        }
        return packed.Length;
    }

    /// <summary>The witness surface of a case, built from the document or the drawn triangles.</summary>
    internal static List<TileWaterWorldTriangle> Witness(TileWaterTopologyCase c, TileWorldDocument doc,
        TileWorldCatalogs catalogs, IReadOnlyList<TileWaterWorldTriangle> drawn)
    {
        float s = doc.TileSize;
        (int x0, int z0, int x1, int z1) = TileBounds(drawn, s);
        return c.Witness switch
        {
            TileWaterTopologyWitness.OtherDiagonal => Split(doc, x0, z0, x1, z1,
                (a, b, d, e) => !TileTriangulation.SplitSwNe(a, b, d, e, TileOverlayShape.Full, 0)),
            TileWaterTopologyWitness.HeightRuleSplit => Split(doc, x0, z0, x1, z1,
                (a, b, d, e) => TileTriangulation.SplitSwNe(a, b, d, e, TileOverlayShape.Full, 0)),
            TileWaterTopologyWitness.FullDetail => FullDetail(doc, catalogs, c.Loads.Select(l => l.Region)),
            _ => drawn.ToList(),
        };
    }

    static (int, int, int, int) TileBounds(IReadOnlyList<TileWaterWorldTriangle> drawn, float s)
    {
        double minX = double.MaxValue, maxX = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
        foreach (TileWaterWorldTriangle t in drawn)
            for (int corner = 0; corner < 3; corner++)
            {
                (double x, _, double z) = t.World(corner);
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minZ = Math.Min(minZ, z);
                maxZ = Math.Max(maxZ, z);
            }
        // World z is minus tile z.
        return ((int)Math.Floor(minX / s), (int)Math.Floor(-maxZ / s), (int)Math.Ceiling(maxX / s), (int)Math.Ceiling(-minZ / s));
    }
}
