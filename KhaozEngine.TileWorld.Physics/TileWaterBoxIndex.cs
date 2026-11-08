using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using I = KhaozEngine.TileWorld.Physics.TileWaterArithmetic;

namespace KhaozEngine.TileWorld.Physics;

// An immutable tree over captured rounded boxes. Bounds use exactly the directed
// enclosure of TileCapsuleBoxPath.Potential, so an omitted box contributes zero error.
internal sealed class TileWaterBoxIndex
{
    readonly record struct Node(Vector3 Minimum, Vector3 Maximum, int Item, int Left, int Right);
    readonly Node[] _nodes;
    internal int Count { get; }

    internal TileWaterBoxIndex(int count, Func<int, (Vector3 Minimum, Vector3 Maximum)> bounds)
    {
        Count = count;
        _nodes = new Node[count == 0 ? 0 : 2 * count - 1];
        if (count == 0) return;
        var boxes = new (Vector3 Minimum, Vector3 Maximum)[count];
        var order = new int[count];
        for (int i = 0; i < count; i++) { boxes[i] = bounds(i); order[i] = i; }
        var byX = Comparer<int>.Create((a, b) => Compare(boxes[a].Minimum.X, boxes[b].Minimum.X, a, b));
        var byZ = Comparer<int>.Create((a, b) => Compare(boxes[a].Minimum.Z, boxes[b].Minimum.Z, a, b));
        int next = 0;
        Build(boxes, order, 0, count, byX, byZ, ref next);
    }

    int Build((Vector3 Minimum, Vector3 Maximum)[] boxes, int[] order, int start, int count,
        IComparer<int> byX, IComparer<int> byZ, ref int next)
    {
        int index = next++;
        var first = boxes[order[start]];
        Vector3 min = first.Minimum, max = first.Maximum;
        for (int i = start + 1; i < start + count; i++)
        {
            min = Vector3.Min(min, boxes[order[i]].Minimum);
            max = Vector3.Max(max, boxes[order[i]].Maximum);
        }
        if (count == 1) { _nodes[index] = new(min, max, order[start], -1, -1); return index; }
        Array.Sort(order, start, count, (double)max.X - min.X >= (double)max.Z - min.Z ? byX : byZ);
        int half = count / 2;
        int left = Build(boxes, order, start, half, byX, byZ, ref next);
        int right = Build(boxes, order, start + half, count - half, byX, byZ, ref next);
        _nodes[index] = new(min, max, -1, left, right);
        return index;
    }

    internal TileWaterCandidates Select(in MovementMediumSweepQuery query, Vector3 origin, Span<int> storage)
    {
        var bounds = new Bounds(query, origin);
        int count = 0;
        if (_nodes.Length != 0 && !Collect(0, bounds, storage, ref count))
            return new(Count, default, true);
        // Tree order is spatial. Coverage publication must retain original source order.
        storage[..count].Sort();
        return new(count, storage[..count], false);
    }

    bool Collect(int index, in Bounds bounds, Span<int> storage, ref int count)
    {
        if (TileWaterQueryWork.Enabled) TileWaterQueryWork.IndexNodes++;
        Node node = _nodes[index];
        if (!bounds.Meets(node.Minimum, node.Maximum)) return true;
        if (node.Item < 0)
            return Collect(node.Left, bounds, storage, ref count) && Collect(node.Right, bounds, storage, ref count);
        if (count == storage.Length) return false;
        storage[count++] = node.Item;
        return true;
    }

    static int Compare(float a, float b, int first, int second)
    {
        int result = a.CompareTo(b);
        return result == 0 ? first.CompareTo(second) : result;
    }

    readonly struct Bounds
    {
        readonly Vector3D _low, _high;
        internal Bounds(in MovementMediumSweepQuery query, Vector3 origin)
        {
            Span<double> low = stackalloc double[3], high = stackalloc double[3];
            for (int axis = 0; axis < 3; axis++)
            {
                I start = I.Point(query.Body.Centre[axis]) + I.Point(origin[axis]);
                I end = start + I.Point(query.Delta[axis]);
                I reach = I.Point(axis == 1 ? query.Body.HalfHeight : query.Body.Radius);
                low[axis] = (I.Point(Math.Min(start.Low, end.Low)) - reach).Low;
                high[axis] = (I.Point(Math.Max(start.High, end.High)) + reach).High;
            }
            _low = new(low[0], low[1], low[2]);
            _high = new(high[0], high[1], high[2]);
        }
        internal bool Meets(Vector3 min, Vector3 max) =>
            !(_low.X > max.X || _high.X < min.X || _low.Y > max.Y || _high.Y < min.Y || _low.Z > max.Z || _high.Z < min.Z);
    }

    readonly record struct Vector3D(double X, double Y, double Z);
}

// A bounded candidate buffer is an optimization only. Overflow enumerates the full
// original range, retaining every existing capacity and precision outcome.
internal readonly ref struct TileWaterCandidates
{
    readonly ReadOnlySpan<int> _items;
    readonly bool _all;
    internal int Count { get; }
    internal TileWaterCandidates(int count, ReadOnlySpan<int> items, bool all)
    {
        Count = count;
        _items = items;
        _all = all;
    }
    internal int this[int index] => _all ? index : _items[index];
}
