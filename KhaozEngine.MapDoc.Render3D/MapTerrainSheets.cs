using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Connects physical patch sheets without aliasing every patch of a joined owner.</summary>
internal static class MapTerrainSheets
{
    internal readonly record struct Node(string Owner, MapPatchKey? Patch);
    readonly record struct Interval(MapWideRational Start, MapWideRational End, Node Sheet);
    internal static Node Key(MapTerrainPartition partition) => new(partition.Face.OwnerId, partition.Face.Patch);

    internal static Dictionary<Node, Node> Connect(IReadOnlyList<MapTerrainPartition> partitions,
        MapScopedSurfaces view, MapSceneAdmission admission, MapPreparationReservation storage)
    {
        // Admit the maps, polygon conversion, line buckets and interval sorting before allocating them.
        storage.Reserve(checked(256L * (view.Surfaces.Count + view.Witness.Present.Count + partitions.Count) +
            1024L * partitions.Sum(p => (long)p.Polygon.Count)));
        var roots = new Dictionary<Node, Node>();
        foreach (var (patch, _) in view.Witness.Present)
        {
            var node = new Node(patch.SurfaceId, patch);
            roots.Add(node, node);
        }
        var lines = new Dictionary<(string Owner, MapTerrainLine Line), List<Interval>>();
        foreach (var partition in partitions)
        {
            var node = Key(partition);
            roots.TryAdd(node, node);
            for (int i = 0; i < partition.Polygon.Count; i++)
            {
                var a = MapWidePoint.From(partition.Polygon[i]);
                var b = MapWidePoint.From(partition.Polygon[(i + 1) % partition.Polygon.Count]);
                if (a.Xz == b.Xz) continue;
                var line = MapTerrainLine.Through(a, b);
                var start = line.Coordinate(a); var end = line.Coordinate(b);
                if (start.CompareTo(end) > 0) (start, end) = (end, start);
                if (!lines.TryGetValue((node.Owner, line), out var intervals)) lines.Add((node.Owner, line), intervals = new());
                intervals.Add(new(start, end, node));
            }
        }
        foreach (var intervals in lines.Values)
        {
            intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
            var current = intervals[0];
            foreach (var next in intervals.Skip(1))
            {
                // A point contact does not connect sheets. Collinear subdivisions may share a longer edge.
                if (next.Start.CompareTo(current.End) < 0)
                {
                    Join(current.Sheet, next.Sheet);
                    if (next.End.CompareTo(current.End) > 0) current = next;
                }
                else current = next;
            }
        }
        var roles = view.Surfaces.ToDictionary(s => s.Id, s => s.Role, StringComparer.Ordinal);
        foreach (var reference in view.Witness.Records)
        {
            if (!view.TryRecord(reference, out var record, out _, borrow: true) || record is not MapSurfaceSeam seam) continue;
            var first = new Node(seam.First.Patch.SurfaceId, seam.First.Patch);
            var second = new Node(seam.Second.Patch.SurfaceId, seam.Second.Patch);
            if (!roots.ContainsKey(first) || !roots.ContainsKey(second) ||
                !roles.TryGetValue(first.Owner, out var role) || !roles.TryGetValue(second.Owner, out var other) || role != other ||
                !MapTerrainSeamValidation.IsValid(seam, view, admission)) continue;
            Join(first, second);
        }
        foreach (var node in roots.Keys.ToArray()) roots[node] = Root(node);
        return roots;

        Node Root(Node node)
        {
            while (roots[node] != node) node = roots[node];
            return node;
        }
        void Join(Node first, Node second)
        {
            first = Root(first); second = Root(second);
            int order = StringComparer.Ordinal.Compare(first.Owner, second.Owner);
            if (order > 0 || order == 0 && Nullable.Compare(first.Patch, second.Patch) > 0) (first, second) = (second, first);
            roots[second] = first;
        }
    }
}
