using System;

namespace KhaozEngine.MapDoc.Surfaces;

/// <summary>The shared boundary rule for counting and emitting exact centroid fans.</summary>
internal static class MapSubdividedTriangle
{
    internal static int CountChildren(MapLatticeTriangle parent, ReadOnlySpan<int> segments)
    {
        int inserted = CountEdge(parent.A, parent.B, segments) + CountEdge(parent.B, parent.C, segments)
            + CountEdge(parent.C, parent.A, segments);
        return inserted == 0 ? 1 : checked(3 + inserted);
    }

    static int CountEdge(MapLatticePoint from, MapLatticePoint to, ReadOnlySpan<int> segments)
    {
        if (!Edge(from, to, out MapCellEdge edge, out int start, out int end)) return 0;
        int k = segments[(int)edge], count = 0;
        for (int i = 1; i < k; i++)
            if (Between(i, k, start, end)) count++;
        return count;
    }

    internal static int Boundary(MapSurfaceVertexBuilder vertices, int x, int z, MapLatticeTriangle parent,
        ReadOnlySpan<int> segments, Span<MapSurfaceVertex> boundary)
    {
        MapSurfaceVertex a = vertices.Point(x, z, parent.A);
        MapSurfaceVertex b = vertices.Point(x, z, parent.B);
        MapSurfaceVertex c = vertices.Point(x, z, parent.C);
        int count = 0;
        AddEdge(vertices, x, z, parent.A, parent.B, a, b, segments, boundary, ref count);
        AddEdge(vertices, x, z, parent.B, parent.C, b, c, segments, boundary, ref count);
        AddEdge(vertices, x, z, parent.C, parent.A, c, a, segments, boundary, ref count);
        return count;
    }

    static void AddEdge(MapSurfaceVertexBuilder vertices, int x, int z, MapLatticePoint fromPoint,
        MapLatticePoint toPoint, MapSurfaceVertex from, MapSurfaceVertex to, ReadOnlySpan<int> segments,
        Span<MapSurfaceVertex> boundary, ref int count)
    {
        boundary[count++] = from;
        if (!Edge(fromPoint, toPoint, out MapCellEdge edge, out int start, out int end)) return;
        int k = segments[(int)edge];
        bool ascending = start < end;
        for (int j = 1; j < k; j++)
        {
            int i = ascending ? j : k - j;
            if (Between(i, k, start, end))
                boundary[count++] = vertices.Insert(x, z, edge, i, k, from, to, start, end);
        }
    }

    static bool Between(int step, int segments, int start, int end) =>
        2 * step > Math.Min(start, end) * segments && 2 * step < Math.Max(start, end) * segments;

    static bool Edge(MapLatticePoint from, MapLatticePoint to, out MapCellEdge edge, out int start, out int end)
    {
        (int ax, int az) = MapSurfaceTopology.LocalTwice(from);
        (int bx, int bz) = MapSurfaceTopology.LocalTwice(to);
        if (az == bz && az is 0 or 2)
        {
            edge = az == 0 ? MapCellEdge.South : MapCellEdge.North;
            start = ax;
            end = bx;
            return true;
        }
        if (ax == bx && ax is 0 or 2)
        {
            edge = ax == 0 ? MapCellEdge.West : MapCellEdge.East;
            start = az;
            end = bz;
            return true;
        }
        edge = default;
        start = end = 0;
        return false;
    }

    internal static MapExactValue Cross(MapExactPoint a, MapExactPoint b, MapExactPoint c) =>
        b.X.Subtract(a.X).Multiply(c.Z.Subtract(a.Z))
            .Subtract(c.X.Subtract(a.X).Multiply(b.Z.Subtract(a.Z)));

    internal static MapExactValue? Height(MapExactTriangle triangle, MapExactValue x, MapExactValue z)
    {
        MapExactPoint a = triangle.A, b = triangle.B, c = triangle.C;
        var p = new MapExactPoint(x, default, z);
        MapExactValue area = Cross(a, b, c);
        MapExactValue ab = Cross(a, b, p), bc = Cross(b, c, p), ca = Cross(c, a, p);
        if (area.Sign == 0 || (area.Sign > 0
            ? ab.Sign < 0 || bc.Sign < 0 || ca.Sign < 0
            : ab.Sign > 0 || bc.Sign > 0 || ca.Sign > 0)) return null;
        return a.Y.Add(b.Y.Subtract(a.Y).Multiply(Cross(a, p, c).Divide(area)))
            .Add(c.Y.Subtract(a.Y).Multiply(ab.Divide(area)));
    }
}
