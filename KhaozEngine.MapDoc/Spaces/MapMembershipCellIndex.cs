using System;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Storage;

namespace KhaozEngine.MapDoc.Spaces;

/// <summary>Exact-bounds traversal with the acquired-cell rank independent of sort order.</summary>
internal sealed class MapMembershipCellIndex
{
    internal readonly record struct Node(MapExactRect Bounds, int First, int Left, int Right, int Rank);
    readonly MapMembershipGeometryArena _arena;
    readonly int[] _cells;
    readonly Node[] _nodes;
    internal long InventoryBytes => checked(64L + 24 + 4L * _cells.Length + 24 + 80L * _nodes.Length);
    internal MapMembershipCellIndex(MapMembershipGeometryArena arena, int[] cells)
    {
        _arena = arena;
        MapMembershipBuild.Inventory(checked(64L + 24 + 4L * cells.Length + 24 + 80L * Math.Max(0, 2 * cells.Length - 1)));
        using var sorting = MapMembershipBuild.Kernel();
        _cells = (int[])cells.Clone();
        _nodes = new Node[cells.Length == 0 ? 0 : checked(2 * cells.Length - 1)];
        int next = 0;
        if (cells.Length != 0) Build(0, cells.Length);
        int Build(int first, int count)
        {
            int index = next++;
            var bounds = Cell(_cells[first]).Polygon.Bounds;
            int rank = Cell(_cells[first]).Id;
            for (int i = first + 1; i < first + count; i++)
            {
                var b = Cell(_cells[i]).Polygon.Bounds;
                bounds = new(MapSpaceGeometry.Min(bounds.MinX, b.MinX), MapSpaceGeometry.Min(bounds.MinZ, b.MinZ),
                    MapSpaceGeometry.Max(bounds.MaxX, b.MaxX), MapSpaceGeometry.Max(bounds.MaxZ, b.MaxZ));
                rank = Math.Min(rank, Cell(_cells[i]).Id);
            }
            int left = -1, right = -1;
            if (count > 1)
            {
                bool x = bounds.MaxX.ToDouble() - bounds.MinX.ToDouble() >= bounds.MaxZ.ToDouble() - bounds.MinZ.ToDouble();
                Array.Sort(_cells, first, count, System.Collections.Generic.Comparer<int>.Create((a, b) =>
                {
                    int order = x ? Cell(a).Polygon.Bounds.MinX.CompareTo(Cell(b).Polygon.Bounds.MinX)
                        : Cell(a).Polygon.Bounds.MinZ.CompareTo(Cell(b).Polygon.Bounds.MinZ);
                    return order != 0 ? order : Cell(a).Id.CompareTo(Cell(b).Id);
                }));
                int half = count / 2;
                left = Build(first, half); right = Build(first + half, count - half);
            }
            _nodes[index] = new(bounds, first, left, right, rank);
            return index;
        }
    }
    MapMembershipPreparedGeometry.Cell Cell(int index) => new(_arena, index);
    internal MapMembershipPreparedGeometry.Cell? Find(MapExactXz point, MapMembershipExact exact)
    {
        MapMembershipPreparedGeometry.Cell? selected = null;
        if (_nodes.Length != 0) Visit(0, point, exact, ref selected);
        return selected;
    }
    void Visit(int index, MapExactXz point, MapMembershipExact exact, ref MapMembershipPreparedGeometry.Cell? selected)
    {
        ref readonly var node = ref _nodes[index];
        if (selected is { } chosen && node.Rank >= chosen.Id ||
            point.X.CompareTo(node.Bounds.MinX) < 0 || point.X.CompareTo(node.Bounds.MaxX) > 0 ||
            point.Z.CompareTo(node.Bounds.MinZ) < 0 || point.Z.CompareTo(node.Bounds.MaxZ) > 0) return;
        if (node.Left < 0)
        {
            var cell = Cell(_cells[node.First]);
            if (cell.Polygon.Contains(point, exact)) selected = cell;
            return;
        }
        Visit(node.Left, point, exact, ref selected);
        Visit(node.Right, point, exact, ref selected);
    }
}
