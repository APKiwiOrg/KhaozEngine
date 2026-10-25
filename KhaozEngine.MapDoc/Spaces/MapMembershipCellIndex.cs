using System;
using System.Linq;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Immutable exact-bounds traversal preserving the original acquired-cell first-match rank.</summary>
internal sealed class MapMembershipCellIndex
{
    readonly record struct Node(MapExactRect Bounds, int First, int Count, int Left, int Right, int Rank);
    readonly MapMembershipPreparedGeometry.Cell[] _cells;
    readonly Node[] _nodes;
    internal long InventoryBytes => checked(64L + 8L * _cells.Length + 96L * _nodes.Length);
    internal MapMembershipCellIndex(MapMembershipPreparedGeometry.Cell[] cells)
    {
        MapMembershipBuild.Inventory(checked(64L + 8L * cells.Length + 96L * Math.Max(0, 2 * cells.Length - 1)));
        using var sorting = MapMembershipBuild.Kernel();
        _cells = cells.ToArray();
        _nodes = new Node[cells.Length == 0 ? 0 : checked(2 * cells.Length - 1)];
        int next = 0;
        if (cells.Length != 0) Build(0, cells.Length);
        int Build(int first, int count)
        {
            int index = next++;
            var bounds = _cells[first].Polygon.Bounds;
            int rank = _cells[first].Id;
            for (int i = first + 1; i < first + count; i++)
            {
                var b = _cells[i].Polygon.Bounds;
                bounds = new(MapSpaceGeometry.Min(bounds.MinX, b.MinX), MapSpaceGeometry.Min(bounds.MinZ, b.MinZ),
                    MapSpaceGeometry.Max(bounds.MaxX, b.MaxX), MapSpaceGeometry.Max(bounds.MaxZ, b.MaxZ));
                rank = Math.Min(rank, _cells[i].Id);
            }
            int left = -1, right = -1;
            if (count > 1)
            {
                bool x = bounds.MaxX.ToDouble() - bounds.MinX.ToDouble() >= bounds.MaxZ.ToDouble() - bounds.MinZ.ToDouble();
                Array.Sort(_cells, first, count, System.Collections.Generic.Comparer<MapMembershipPreparedGeometry.Cell>.Create((a, b) =>
                {
                    int order = x ? a.Polygon.Bounds.MinX.CompareTo(b.Polygon.Bounds.MinX) : a.Polygon.Bounds.MinZ.CompareTo(b.Polygon.Bounds.MinZ);
                    return order != 0 ? order : a.Id.CompareTo(b.Id);
                }));
                int half = count / 2;
                left = Build(first, half); right = Build(first + half, count - half);
            }
            _nodes[index] = new(bounds, first, count, left, right, rank);
            return index;
        }
    }
    internal MapMembershipPreparedGeometry.Cell? Find(MapExactXz point, MapMembershipExact exact)
    {
        MapMembershipPreparedGeometry.Cell? selected = null;
        if (_nodes.Length != 0) Visit(0, point, exact, ref selected);
        return selected;
    }
    void Visit(int index, MapExactXz point, MapMembershipExact exact, ref MapMembershipPreparedGeometry.Cell? selected)
    {
        ref readonly var node = ref _nodes[index];
        if (selected is not null && node.Rank >= selected.Id ||
            point.X.CompareTo(node.Bounds.MinX) < 0 || point.X.CompareTo(node.Bounds.MaxX) > 0 ||
            point.Z.CompareTo(node.Bounds.MinZ) < 0 || point.Z.CompareTo(node.Bounds.MaxZ) > 0) return;
        if (node.Count == 1)
        {
            var cell = _cells[node.First];
            if (cell.Polygon.Contains(point, exact)) selected = cell;
            return;
        }
        Visit(node.Left, point, exact, ref selected);
        Visit(node.Right, point, exact, ref selected);
    }
}
