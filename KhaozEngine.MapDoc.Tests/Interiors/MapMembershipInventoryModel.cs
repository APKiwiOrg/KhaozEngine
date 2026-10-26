using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;

namespace KhaozEngine.Tests.MapDoc.Interiors;

/// <summary>A conservative simultaneous inventory subtotal. No receipt, peak or production byte calculator is read.</summary>
internal static class MapMembershipInventoryModel
{
    // Three simultaneously active polygon, edge and normalization arithmetic kernels.
    internal const long FixedKernelAllowance = 3 * 32768;
    // Pinned design layouts, independently specified here. Capacities include unused rows.
    internal const int TokenBytes = 16, PolygonBytes = 80, VertexBytes = 32, CornerBytes = 1,
        TriangleBytes = 160, FaceBytes = 32, CellBytes = 64, BoundBytes = 32, IndexBytes = 80;
    internal readonly record struct Columns(int Polygons, int Corners, int Triangles, int Faces, int Cells,
        int Bounds, int Affines, int IndexNodes, int MagnitudeCapacity, long SideTables);
    internal static long ArrayBytes(long count, int stride) => checked((24 + count * stride + 7) & ~7L);
    internal static long CompactColumns(Columns c) => checked(ArrayBytes(c.Polygons, PolygonBytes) +
        ArrayBytes(c.Corners, VertexBytes) + ArrayBytes(c.Corners, CornerBytes) +
        ArrayBytes(c.Triangles, TriangleBytes) + ArrayBytes(c.Faces, FaceBytes) + ArrayBytes(c.Cells, CellBytes) +
        ArrayBytes(c.Bounds, BoundBytes) + ArrayBytes(4L * c.Affines, TokenBytes) +
        ArrayBytes(c.IndexNodes, IndexBytes) + ArrayBytes(c.MagnitudeCapacity, 1) + c.SideTables);
    internal static long ReplacementOverlap(Columns old, Columns replacement, long workspace) =>
        checked(CompactColumns(old) + CompactColumns(replacement) + workspace);
    internal static long Header(MapScopedScene scene)
    {
        var records = MapSpaceGeometry.Records(scene.SurfaceView).ToArray();
        int roots = records.OfType<MapSpaceFootprint>().Count(), prisms = records.OfType<MapPrismDomain>().Count();
        int parts = Math.Max(1, roots * (prisms + 1));
        return checked(4096 + 12640 + 512 * scene.SurfaceView.Surfaces.Count +
            records.Sum(r => 4096 + 16L * r.Id.Length) + 16L * records.Length * records.Length +
            256 + parts * (2L * Unsafe.SizeOf<MapMembershipPart>() + 5) + prisms);
    }
    internal static long SimultaneousSubtotal(MapScopedScene scene)
    {
        var view = scene.SurfaceView;
        long bytes = Header(scene);
        // These fixtures select every authored cell. Geometry is enumerated before membership, without admission.
        foreach (var present in view.Witness.Present)
        {
            var patch = view.Patch(present.Key).Patch!;
            var surface = view.Surfaces.Single(s => s.Id == patch.Key.SurfaceId);
            var local = MapSurfaceCompiler.Compile(surface, patch);
            var world = MapEffectiveGeometry.TransformPatch(view, local);
            for (int z = 0; z < patch.Depth; z++)
                for (int x = 0; x < patch.Width; x++)
                {
                    int slot = (z + patch.CellMinZ) * 64 + x + patch.CellMinX;
                    var faces = world.Faces.Where(f => f.Key.Primitive == slot).ToArray();
                    bytes += 256 + 256L * faces.Length + Polygon(MapEffectiveGeometry.CellPolygon(view, patch.Key, slot));
                    foreach (var face in faces) bytes += Triangle(world.ExactTriangle(face));
                }
        }
        foreach (var footprint in MapSpaceGeometry.Records(view).OfType<MapSpaceFootprint>())
            foreach (int slot in footprint.SlotCells)
                bytes += Polygon(MapEffectiveGeometry.CellPolygon(view, footprint.Lattice, slot));
        // Header and exact cell/footprint outputs coexist before publication. Validation also retains its
        // control rows, compiled domain outputs and two copied root triangles simultaneously.
        var records = MapSpaceGeometry.Records(view).ToArray();
        var prisms = records.OfType<MapPrismDomain>().ToArray();
        long validation = prisms.Length == 0 ? 0 : Header(scene) + 32768 + 256L * records.Length +
            prisms.Sum(p => 4096 + 256L * (4L * p.Footprint.Rects.Count + p.Footprint.Rings.Sum(r => r.Vertices.Count))) +
            2 * (1024 + 3 * 256) + MapMembershipValidationInventory.Additional(view, prisms);
        // Excluding other retained rows, scene obligations and temporary owners makes a passing bound stronger.
        return Math.Max(bytes, validation);
    }
    static long Polygon(IReadOnlyList<MapExactXz> polygon)
    {
        long bytes = 128 + 45L * polygon.Count;
        for (int i = 0; i < polygon.Count; i++) bytes += Edge(polygon[i], polygon[(i + 1) % polygon.Count]);
        return bytes;
    }
    static long Triangle(MapExactTriangle t) => 219 +
        Edge(new(t.A.X, t.A.Z), new(t.B.X, t.B.Z)) + Edge(new(t.B.X, t.B.Z), new(t.C.X, t.C.Z)) +
        Edge(new(t.C.X, t.C.Z), new(t.A.X, t.A.Z)) + Plane(t);
    static long Edge(MapExactXz a, MapExactXz b)
    {
        BigInteger denominator = Common([a.X, a.Z, b.X, b.Z]);
        BigInteger ax = a.X.Numerator * (denominator / a.X.Denominator), az = a.Z.Numerator * (denominator / a.Z.Denominator);
        BigInteger bx = b.X.Numerator * (denominator / b.X.Denominator), bz = b.Z.Numerator * (denominator / b.Z.Denominator);
        return Affine([(az - bz) * denominator, (bx - ax) * denominator, ax * bz - az * bx, BigInteger.One]);
    }
    static long Plane(MapExactTriangle t)
    {
        BigInteger denominator = Common([t.A.X, t.A.Y, t.A.Z, t.B.X, t.B.Y, t.B.Z, t.C.X, t.C.Y, t.C.Z]);
        BigInteger[] a = Clear(t.A), b = Clear(t.B), c = Clear(t.C);
        BigInteger[] u = [b[0] - a[0], b[1] - a[1], b[2] - a[2]], v = [c[0] - a[0], c[1] - a[1], c[2] - a[2]];
        BigInteger[] normal = [u[1] * v[2] - u[2] * v[1], u[2] * v[0] - u[0] * v[2], u[0] * v[1] - u[1] * v[0]];
        return Affine([-normal[0] * denominator, -normal[2] * denominator,
            normal[0] * a[0] + normal[1] * a[1] + normal[2] * a[2], normal[1] * denominator]);
        BigInteger[] Clear(MapExactPoint p) => [p.X.Numerator * (denominator / p.X.Denominator),
            p.Y.Numerator * (denominator / p.Y.Denominator), p.Z.Numerator * (denominator / p.Z.Denominator)];
    }
    static BigInteger Common(MapExactValue[] values) => values.Aggregate(BigInteger.One,
        (current, value) => current / BigInteger.GreatestCommonDivisor(current, value.Denominator) * value.Denominator);
    static long Affine(BigInteger[] values)
    {
        BigInteger divisor = values.Aggregate(BigInteger.Zero, (current, value) =>
            BigInteger.GreatestCommonDivisor(current, BigInteger.Abs(value)));
        return 64 + values.Sum(value => 48L + BigInteger.Abs(value / divisor).GetByteCount(isUnsigned: true));
    }
}
