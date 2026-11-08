using System;
using System.Collections.Generic;

namespace KhaozEngine.Movement;

/// <summary>Inclusive projected bounds only. Exact clipping and overlap remain the authority.</summary>
internal sealed class DescentFaceIndex
{
    readonly Bounds[] _bounds;
    readonly int[] _ordinals, _results;
    readonly Node[] _nodes;
    readonly AxisComparer _x, _z;
    int _nodeCount, _resultCount;
    readonly record struct Bounds(double MinX, double MinZ, double MaxX, double MaxZ)
    {
        internal bool Touches(Bounds other) => MinX <= other.MaxX && MaxX >= other.MinX && MinZ <= other.MaxZ && MaxZ >= other.MinZ;
        internal Bounds Union(Bounds b) => new(Math.Min(MinX, b.MinX), Math.Min(MinZ, b.MinZ), Math.Max(MaxX, b.MaxX), Math.Max(MaxZ, b.MaxZ));
    }
    readonly record struct Node(Bounds Bounds, int Start, int Count, int Left, int Right);
    sealed class AxisComparer(Bounds[] bounds, bool x) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            var p = bounds[a]; var q = bounds[b];
            int order = (x ? p.MinX + p.MaxX : p.MinZ + p.MaxZ).CompareTo(x ? q.MinX + q.MaxX : q.MinZ + q.MaxZ);
            return order != 0 ? order : a.CompareTo(b);
        }
    }
    internal DescentFaceIndex(DescentFace[] faces, DescentCaptureIndexBudget? budget = null)
    {
        budget ??= new();
        long nodes = faces.Length == 0 ? 0 : 2L * faces.Length - 1;
        if (nodes > Array.MaxLength) throw new PhysicsNavDescentEvidenceException(long.MaxValue, Array.MaxLength);
        budget.Reserve(faces.Length, 32 + 4 + 4);
        budget.Reserve(nodes, 48);
        _bounds = new Bounds[faces.Length]; _ordinals = new int[faces.Length]; _results = new int[faces.Length]; _nodes = new Node[(int)nodes];
        for (int i = 0; i < faces.Length; i++)
        {
            _ordinals[i] = i;
            var face = faces[i];
            if (face.Points.Length == 0)
            {
                double extent = face.Radius + face.HalfLength;
                _bounds[i] = new(face.Pose.Position.X - extent, face.Pose.Position.Z - extent, face.Pose.Position.X + extent, face.Pose.Position.Z + extent);
            }
            else
            {
                double minX = double.PositiveInfinity, minZ = minX, maxX = double.NegativeInfinity, maxZ = maxX;
                foreach (var p in face.Points) { minX = Math.Min(minX, p.X); minZ = Math.Min(minZ, p.Z); maxX = Math.Max(maxX, p.X); maxZ = Math.Max(maxZ, p.Z); }
                _bounds[i] = new(minX, minZ, maxX, maxZ);
            }
        }
        _x = new(_bounds, true); _z = new(_bounds, false);
        if (faces.Length != 0) Build(0, faces.Length);
    }
    int Build(int start, int count)
    {
        int index = _nodeCount++;
        var bounds = _bounds[_ordinals[start]];
        for (int i = start + 1; i < start + count; i++) bounds = bounds.Union(_bounds[_ordinals[i]]);
        if (count <= 8) { _nodes[index] = new(bounds, start, count, -1, -1); return index; }
        Array.Sort(_ordinals, start, count, bounds.MaxX - bounds.MinX >= bounds.MaxZ - bounds.MinZ ? _x : _z);
        int half = count / 2;
        int left = Build(start, half), right = Build(start + half, count - half);
        _nodes[index] = new(bounds, 0, 0, left, right);
        return index;
    }
    internal ReadOnlySpan<int> Query(double minX, double minZ, double maxX, double maxZ)
    {
        _resultCount = 0;
        if (_nodeCount != 0) Visit(0, new(minX, minZ, maxX, maxZ));
        Array.Sort(_results, 0, _resultCount);
        return _results.AsSpan(0, _resultCount);
    }
    void Visit(int index, Bounds query)
    {
        var node = _nodes[index];
        if (!node.Bounds.Touches(query)) return;
        if (node.Count == 0) { Visit(node.Left, query); Visit(node.Right, query); return; }
        for (int i = node.Start; i < node.Start + node.Count; i++)
        {
            int face = _ordinals[i];
            if (_bounds[face].Touches(query)) _results[_resultCount++] = face;
        }
    }
}
