using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Attachments;

// The rest-space tree retains its topology. Its refitted bounds enclose the
// opening facets and all closing facets inside a small checked coverage slab.
internal sealed class ClothSelfBroadPhase
{
    private const float ReusePadding = .002f;
    private readonly ClothRig _rig;
    private readonly Node[] _nodes;
    private readonly ulong[] _leafSets;
    private readonly ulong[] _exclusionSets;
    private readonly int _words;
    private readonly ClothBounds[] _bounds;
    private readonly ClothBounds[] _coverage;
    private readonly Pair[] _pairs;
    private readonly int[] _supportFaces;
    private int _supportFaceCount;
    private int _pairCount;
    private bool _built;

    internal ClothSelfBroadPhase(ClothRig rig, int maxVisits)
    {
        _rig = rig;
        int faceCount = rig.Triangles.Length;
        _nodes = new Node[2 * faceCount - 1];
        _bounds = new ClothBounds[_nodes.Length];
        _coverage = new ClothBounds[rig.RestPositions.Length];
        _pairs = new Pair[maxVisits];
        _supportFaces = new int[faceCount];
        _words = (faceCount + 63) / 64;
        _leafSets = new ulong[_words * _nodes.Length];
        _exclusionSets = new ulong[_leafSets.Length];
        var order = new int[faceCount];
        var center = new Vector3[faceCount];
        for (int i = 0; i < faceCount; i++)
        {
            order[i] = i;
            center[i] = rig.Triangles[i].Blend(rig.RestPositions, new Vector3(1f / 3));
        }
        int count = 0;
        Build(0, faceCount, order, center, ref count);
        BuildExclusionSets(BuildExclusions(rig));
    }

    internal ReadOnlySpan<Pair> Pairs => _pairs.AsSpan(0, _pairCount);
    internal ReadOnlySpan<int> SupportFaces => _supportFaces.AsSpan(0, _supportFaceCount);

    internal bool CollectSupport(in ClothBounds bounds, ClothContactWorkspace work)
    {
        _supportFaceCount = 0;
        if (!CollectSupportNode(0, bounds, work)) return false;
        Array.Sort(_supportFaces, 0, _supportFaceCount);
        return true;
    }

    private bool CollectSupportNode(int nodeIndex, in ClothBounds bounds, ClothContactWorkspace work)
    {
        if (!work.Visit()) return false;
        if (!_bounds[nodeIndex].Overlaps(bounds)) return true;
        Node node = _nodes[nodeIndex];
        if (node.Leaf < 0)
            return CollectSupportNode(node.Left, bounds, work) && CollectSupportNode(node.Right, bounds, work);

        _supportFaces[_supportFaceCount++] = node.Leaf;
        return true;
    }

    internal void Begin() => _built = false;

    internal bool EnsureCoverage(Vector3[] opening, Vector3[] closing, float thickness, ClothContactWorkspace work)
    {
        if (_built)
        {
            bool covered = true;
            for (int i = 0; i < closing.Length; i++)
            {
                if (!work.Visit()) return false;
                Vector3 point = closing[i];
                ClothBounds bounds = _coverage[i];
                if (point.X < bounds.Min.X || point.Y < bounds.Min.Y || point.Z < bounds.Min.Z ||
                    point.X > bounds.Max.X || point.Y > bounds.Max.Y || point.Z > bounds.Max.Z)
                {
                    covered = false;
                    break;
                }
            }
            if (covered) return true;
        }
        for (int i = 0; i < closing.Length; i++)
        {
            if (!work.Visit()) return false;
            _coverage[i] = new ClothBounds(closing[i], closing[i]).Expand(ReusePadding);
        }
        for (int i = _nodes.Length - 1; i >= 0; i--)
        {
            if (!work.Visit()) return false;
            Node node = _nodes[i];
            if (node.Leaf >= 0)
            {
                ClothTriangle face = _rig.Triangles[node.Leaf];
                ClothBounds bounds = new ClothFacet(face, opening).Bounds;
                bounds = ClothBounds.Enclose(bounds, _coverage[face.A]);
                bounds = ClothBounds.Enclose(bounds, _coverage[face.B]);
                _bounds[i] = ClothBounds.Enclose(bounds, _coverage[face.C]).Expand(thickness);
            }
            else
                _bounds[i] = ClothBounds.Enclose(_bounds[node.Left], _bounds[node.Right]);
        }
        _pairCount = 0;
        _built = Collect(0, 0, work);
        return _built;
    }

    private bool Collect(int a, int b, ClothContactWorkspace work)
    {
        if (!work.Visit()) return false;
        if (AllNeighbours(a, b)) return true;
        Node first = _nodes[a], last = _nodes[b];
        if (a == b)
            return first.Leaf >= 0 || (Collect(first.Left, first.Left, work) &&
                Collect(first.Right, first.Right, work) && Collect(first.Left, first.Right, work));

        if (!_bounds[a].Overlaps(_bounds[b])) return true;
        if (first.Leaf >= 0 && last.Leaf >= 0)
        {
            _pairs[_pairCount++] = new Pair(first.Leaf, last.Leaf);
            return true;
        }

        if (first.Count >= last.Count)
            return Collect(first.Left, b, work) && Collect(first.Right, b, work);
        return Collect(a, last.Left, work) && Collect(a, last.Right, work);
    }

    private bool AllNeighbours(int first, int second)
    {
        for (int word = 0; word < _words; word++)
            if ((_leafSets[second * _words + word] & ~_exclusionSets[first * _words + word]) != 0)
                return false;
        return true;
    }

    private void BuildExclusionSets(bool[] excluded)
    {
        int faces = _rig.Triangles.Length;
        for (int i = _nodes.Length - 1; i >= 0; i--)
        {
            Node node = _nodes[i];
            int offset = i * _words;
            if (node.Leaf >= 0)
            {
                _leafSets[offset + node.Leaf / 64] = 1UL << (node.Leaf % 64);
                for (int face = 0; face < faces; face++)
                    if (excluded[node.Leaf * faces + face])
                        _exclusionSets[offset + face / 64] |= 1UL << (face % 64);
                continue;
            }

            int left = node.Left * _words, right = node.Right * _words;
            for (int word = 0; word < _words; word++)
            {
                _leafSets[offset + word] = _leafSets[left + word] | _leafSets[right + word];
                _exclusionSets[offset + word] = _exclusionSets[left + word] & _exclusionSets[right + word];
            }
        }
    }

    private int Build(int start, int length, int[] faces, Vector3[] centers, ref int next)
    {
        int index = next++;
        if (length == 1)
        {
            _nodes[index] = new Node(1, faces[start], -1, -1);
            return index;
        }
        Vector3 min = centers[faces[start]], max = min;
        for (int i = start + 1; i < start + length; i++)
        {
            min = Vector3.Min(min, centers[faces[i]]);
            max = Vector3.Max(max, centers[faces[i]]);
        }
        Vector3 span = max - min;
        int axis = span.X >= MathF.Max(span.Y, span.Z) ? 0 : span.Y >= span.Z ? 1 : 2;
        Array.Sort(faces, start, length, Comparer<int>.Create((a, b) =>
        {
            int order = centers[a][axis].CompareTo(centers[b][axis]);
            return order != 0 ? order : a.CompareTo(b);
        }));
        int split = length / 2;
        int left = Build(start, split, faces, centers, ref next);
        int right = Build(start + split, length - split, faces, centers, ref next);
        _nodes[index] = new Node(length, -1, left, right);
        return index;
    }

    private static bool[] BuildExclusions(ClothRig rig)
    {
        int size = rig.RestPositions.Length;
        bool[] neighbours = new bool[size * size];
        for (int i = 0; i < size; i++) neighbours[i * size + i] = true;
        foreach (ClothTriangle triangle in rig.Triangles)
        {
            Link(triangle.A, triangle.B);
            Link(triangle.B, triangle.C);
            Link(triangle.C, triangle.A);
        }

        ReadOnlySpan<ClothTriangle> faces = rig.Triangles;
        bool[] excluded = new bool[faces.Length * faces.Length];
        for (int i = 0; i < faces.Length; i++)
        for (int j = i; j < faces.Length; j++)
        {
            ClothTriangle a = faces[i], b = faces[j];
            bool neighbour = Adjacent(a.A, b) || Adjacent(a.B, b) || Adjacent(a.C, b);
            excluded[i * faces.Length + j] = excluded[j * faces.Length + i] = neighbour;
        }
        return excluded;

        void Link(int a, int b) => neighbours[a * size + b] = neighbours[b * size + a] = true;

        bool Adjacent(int a, ClothTriangle b) =>
            neighbours[a * size + b.A] || neighbours[a * size + b.B] || neighbours[a * size + b.C];
    }

    internal readonly record struct Pair(int First, int Last);
    private readonly record struct Node(int Count, int Leaf, int Left, int Right);
}
