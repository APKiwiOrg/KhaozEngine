using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;

namespace KhaozEngine.TileWorld.Physics;

// Immutable bounds tree over the same rounded columns used to construct trace coverage.
internal sealed class TileWaterColumnIndex
{
    readonly Node[] _nodes;
    readonly record struct Node(Vector3 Minimum, Vector3 Maximum, int Column, int Left, int Right);

    internal TileWaterColumnIndex(TileWaterColumn[] columns)
    {
        _nodes = new Node[columns.Length == 0 ? 0 : 2 * columns.Length - 1];
        if (columns.Length == 0) return;
        var order = new int[columns.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        var byX = Comparer<int>.Create((a, b) => Compare(columns[a].Minimum.X, columns[b].Minimum.X, a, b));
        var byZ = Comparer<int>.Create((a, b) => Compare(columns[a].Minimum.Z, columns[b].Minimum.Z, a, b));
        int next = 0;
        Build(columns, order, 0, order.Length, byX, byZ, ref next);
    }

    int Build(TileWaterColumn[] columns, int[] order, int start, int count,
        IComparer<int> byX, IComparer<int> byZ, ref int next)
    {
        int index = next++;
        TileWaterColumn first = columns[order[start]];
        Vector3 min = first.Minimum, max = first.Maximum;
        for (int i = start + 1; i < start + count; i++)
        {
            min = Vector3.Min(min, columns[order[i]].Minimum);
            max = Vector3.Max(max, columns[order[i]].Maximum);
        }
        if (count == 1)
        {
            _nodes[index] = new(min, max, order[start], -1, -1);
            return index;
        }
        var comparer = (double)max.X - min.X >= (double)max.Z - min.Z ? byX : byZ;
        Array.Sort(order, start, count, comparer);
        int half = count / 2;
        int left = Build(columns, order, start, half, byX, byZ, ref next);
        int right = Build(columns, order, start + half, count - half, byX, byZ, ref next);
        _nodes[index] = new(min, max, -1, left, right);
        return index;
    }

    internal int FindContaining(in MovementBodyQuery body, Vector3 origin) =>
        _nodes.Length == 0 ? -1 : FindContaining(0, body, origin);

    internal int Collect(in TileWaterPathBounds bounds, Span<int> columns)
    {
        int count = 0;
        return _nodes.Length == 0 || Collect(0, bounds, columns, ref count) ? count : -1;
    }

    bool Collect(int index, in TileWaterPathBounds bounds, Span<int> columns, ref int count)
    {
        Node node = _nodes[index];
        if (!bounds.Meets(node.Minimum, node.Maximum)) return true;
        if (node.Column < 0)
            return Collect(node.Left, bounds, columns, ref count) && Collect(node.Right, bounds, columns, ref count);
        if (count == columns.Length) return false;
        columns[count++] = node.Column;
        return true;
    }

    int FindContaining(int index, in MovementBodyQuery body, Vector3 origin)
    {
        Node node = _nodes[index];
        // Every ancestor encloses the actual rounded leaf bounds. Exact comparisons prune only outside that
        // enclosure, so every containing column reaches a leaf regardless of float tile-address spacing.
        // Candidate membership is X [minimum, maximum), Z (minimum, maximum] and feet below surface.
        // The captured drawn triangle supplies the final lower boundary.
        if (!Contains(node.Minimum, node.Maximum, body, origin)) return -1;
        if (node.Column >= 0) return node.Column;
        int left = FindContaining(node.Left, body, origin);
        return left >= 0 ? left : FindContaining(node.Right, body, origin);
    }

    static bool Contains(Vector3 min, Vector3 max, in MovementBodyQuery body, Vector3 origin)
    {
        Vector3 centre = body.Centre;
        float half = -body.HalfHeight;
        return TileWaterArithmetic.Sign(centre.X, origin.X, -min.X) >= 0 &&
            TileWaterArithmetic.Sign(centre.X, origin.X, -max.X) < 0 &&
            TileWaterArithmetic.Sign(centre.Z, origin.Z, -min.Z) > 0 &&
            TileWaterArithmetic.Sign(centre.Z, origin.Z, -max.Z) <= 0 &&
            TileWaterArithmetic.Sign(centre.Y, origin.Y, half, -max.Y) < 0;
    }

    static int Compare(float a, float b, int first, int second)
    {
        int result = a.CompareTo(b);
        return result == 0 ? first.CompareTo(second) : result;
    }
}
