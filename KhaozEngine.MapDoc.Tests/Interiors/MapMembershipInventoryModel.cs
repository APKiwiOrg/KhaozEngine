using System;
using System.Linq;
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
    internal static long SimultaneousSubtotal(MapScopedScene scene, string shape)
    {
        var view = scene.SurfaceView;
        long bytes = MapMembershipArenaInventoryModel.Subtotal(scene, shape);
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
}
