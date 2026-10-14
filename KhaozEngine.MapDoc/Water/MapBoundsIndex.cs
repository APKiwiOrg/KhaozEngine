using System;
using System.Collections.Generic;
using System.Linq;

namespace KhaozEngine.MapDoc.Water;

/// <summary>A static exact index over boxes, built once and queried many times. A query returns, in ascending index
/// order, the boxes whose bounds overlap the query box with positive extent on both axes. Nodes split their boxes by
/// minimum X and minimum Z in turn and keep the union of their bounds, so a query visits only nodes whose union
/// overlaps it. Every decision is an exact comparison, and ties split by index, so the result is deterministic.</summary>
internal sealed class MapBoundsIndex
{
    const int Leaf = 8;
    readonly IReadOnlyList<MapWaterPolygons.Bounds> _boxes;
    readonly int[] _order;
    readonly List<Node> _nodes = new();

    internal MapBoundsIndex(IReadOnlyList<MapWaterPolygons.Bounds> boxes)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        _boxes = boxes;
        _order = Enumerable.Range(0, boxes.Count).ToArray();
        if (boxes.Count != 0) Build(0, boxes.Count, splitX: true);
    }

    internal List<int> Overlapping(MapWaterPolygons.Bounds query)
    {
        var result = new List<int>();
        if (_nodes.Count == 0) return result;
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count != 0)
        {
            var node = _nodes[pending.Pop()];
            // A box that overlaps the query lies inside its node's union, so that union overlaps the query too.
            if (!node.Union.OverlapsOpen(query)) continue;
            if (node.Left < 0)
            {
                for (int i = node.Low; i < node.High; i++)
                    if (_boxes[_order[i]].OverlapsOpen(query)) result.Add(_order[i]);
            }
            else
            {
                pending.Push(node.Left);
                pending.Push(node.Right);
            }
        }
        result.Sort();
        return result;
    }

    int Build(int low, int high, bool splitX)
    {
        var union = _boxes[_order[low]];
        for (int i = low + 1; i < high; i++) union = union.Include(_boxes[_order[i]].Min).Include(_boxes[_order[i]].Max);
        int index = _nodes.Count;
        _nodes.Add(new(low, high, -1, -1, union));
        if (high - low <= Leaf) return index;
        Array.Sort(_order, low, high - low, Comparer<int>.Create((a, b) =>
        {
            int order = splitX ? _boxes[a].Min.X.CompareTo(_boxes[b].Min.X) : _boxes[a].Min.Z.CompareTo(_boxes[b].Min.Z);
            return order != 0 ? order : a.CompareTo(b);
        }));
        int middle = low + (high - low) / 2;
        int left = Build(low, middle, !splitX);
        int right = Build(middle, high, !splitX);
        _nodes[index] = new(low, high, left, right, union);
        return index;
    }

    readonly record struct Node(int Low, int High, int Left, int Right, MapWaterPolygons.Bounds Union);
}
