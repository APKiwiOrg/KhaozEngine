using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Markers;

/// <summary>The work one rectangle query did: index nodes taken off the traversal stack and points given the exact test.</summary>
internal sealed record MapMarkerQueryWork(int NodesVisited, int PointsTested);

/// <summary>R6-P4. An immutable median-split 2D point hierarchy over marker XZ, built once per registry. Each split takes
/// the longer XZ extent of its node and orders points by exact coordinate, then ordinal ID. Node bounds are rounded
/// outward from the exact points, as <see cref="MapEffectiveAnnotations"/> rounds marker boxes, so culling never drops a
/// point the exact half-open test would accept. Leaves hold at most eight points.</summary>
internal sealed class MapMarkerSpatialIndex
{
    const int LeafSize = 8;
    readonly Point[] _points;
    readonly List<Node> _nodes = new();

    readonly record struct Point(MapEffectiveMarker Marker, MapWideRational X, MapWideRational Z, double MinX, double MinZ, double MaxX, double MaxZ);
    readonly record struct Node(double MinX, double MinZ, double MaxX, double MaxZ, int Start, int Count, int Left, int Right)
    {
        internal bool Leaf => Left < 0;
    }

    internal MapMarkerSpatialIndex(IReadOnlyList<MapEffectiveMarker> markers)
    {
        _points = markers.Select(m =>
        {
            var box = MapEffectiveAnnotations.Bounds(m);
            return new Point(m, MapWideRational.From(m.Position.X), MapWideRational.From(m.Position.Z), box.MinX, box.MinZ, box.MaxX, box.MaxZ);
        }).ToArray();
        if (_points.Length != 0) _ = Build(0, _points.Length);
    }

    /// <summary>Exact half-open candidates in ordinal ID order.</summary>
    internal IReadOnlyList<MapEffectiveMarker> Query(MapMarkerRect rect, bool enabledOnly, out MapMarkerQueryWork work)
    {
        var minX = MapWideRational.FromDouble(rect.MinX);
        var minZ = MapWideRational.FromDouble(rect.MinZ);
        var maxX = MapWideRational.FromDouble(rect.MaxX);
        var maxZ = MapWideRational.FromDouble(rect.MaxZ);
        var result = new List<MapEffectiveMarker>();
        int visited = 0, tested = 0;
        var stack = new Stack<int>();
        if (_nodes.Count != 0) stack.Push(0);
        while (stack.Count != 0)
        {
            Node node = _nodes[stack.Pop()];
            visited++;
            // Closed outward node bounds against the half-open rectangle.
            if (node.MaxX < rect.MinX || node.MinX >= rect.MaxX || node.MaxZ < rect.MinZ || node.MinZ >= rect.MaxZ) continue;
            if (!node.Leaf)
            {
                stack.Push(node.Right);
                stack.Push(node.Left);
                continue;
            }
            for (int i = node.Start; i < node.Start + node.Count; i++)
            {
                Point point = _points[i];
                if (enabledOnly && !point.Marker.Enabled) continue;
                tested++;
                if (point.X.CompareTo(minX) >= 0 && point.X.CompareTo(maxX) < 0 && point.Z.CompareTo(minZ) >= 0 && point.Z.CompareTo(maxZ) < 0)
                    result.Add(point.Marker);
            }
        }
        work = new(visited, tested);
        result.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        return result;
    }

    int Build(int start, int count)
    {
        double minX = double.PositiveInfinity, minZ = double.PositiveInfinity, maxX = double.NegativeInfinity, maxZ = double.NegativeInfinity;
        for (int i = start; i < start + count; i++)
        {
            Point point = _points[i];
            minX = Math.Min(minX, point.MinX); minZ = Math.Min(minZ, point.MinZ);
            maxX = Math.Max(maxX, point.MaxX); maxZ = Math.Max(maxZ, point.MaxZ);
        }
        int index = _nodes.Count;
        _nodes.Add(new(minX, minZ, maxX, maxZ, start, count, -1, -1));
        if (count <= LeafSize) return index;
        bool alongX = maxX - minX >= maxZ - minZ;
        Array.Sort(_points, start, count, Comparer<Point>.Create((a, b) =>
        {
            int order = alongX ? a.X.CompareTo(b.X) : a.Z.CompareTo(b.Z);
            return order != 0 ? order : StringComparer.Ordinal.Compare(a.Marker.Id, b.Marker.Id);
        }));
        int half = count / 2;
        int left = Build(start, half);
        int right = Build(start + half, count - half);
        _nodes[index] = _nodes[index] with { Left = left, Right = right };
        return index;
    }
}
