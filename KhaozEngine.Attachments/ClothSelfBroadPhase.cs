using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Attachments;

// The rest-space tree retains its topology. Its refitted bounds enclose the
// opening facets and all closing facets inside a small checked coverage slab.
internal sealed class ClothSelfBroadPhase
{
    private const float ReusePadding = .002f;
    private static readonly ConditionalWeakTable<ClothRig, Topology> Topologies = new();

    private readonly ClothRig _rig;
    private readonly Node[] _nodes;
    private readonly PairNode[] _selfTraversal;
    private readonly ulong[] _positionDependencies;
    private readonly ulong[] _movedPositions;
    private readonly ulong[] _affectedBounds;
    private readonly ClothBounds[] _bounds;
    private readonly ClothBounds[] _coverage;
    private readonly Pair[] _pairs;
    private readonly int[] _supportFaces;
    private readonly int[] _supportCounts;
    private readonly ClothBounds[] _queriedBounds;
    private readonly int[] _queriedSupports;
    private readonly ulong[] _pendingSupportFaces;
    private readonly ulong[] _changedFaces;
    private readonly int[] _faceNodes;
    private int _queriedCount;
    private int _pairCount;
    private int _movedCount;
    private bool _workRefused;
    private bool _built;
    private bool _pairsCurrent;
    private Vector3[]? _proposedCoverage;

    internal ClothSelfBroadPhase(ClothRig rig, int maxVisits, int supports)
    {
        _rig = rig;
        int faceCount = rig.Triangles.Length;
        Topology topology = GetTopology(rig);
        _nodes = topology.Nodes;
        _selfTraversal = topology.SelfTraversal;
        _positionDependencies = topology.PositionDependencies;
        _movedPositions = new ulong[(rig.RestPositions.Length + 63) / 64];
        _affectedBounds = new ulong[(_nodes.Length + 63) / 64];
        _bounds = new ClothBounds[_nodes.Length];
        _coverage = new ClothBounds[rig.RestPositions.Length];
        _pairs = new Pair[maxVisits];
        _supportFaces = new int[supports * faceCount];
        _supportCounts = new int[supports];
        _queriedBounds = new ClothBounds[supports];
        _queriedSupports = new int[supports];
        _changedFaces = new ulong[(faceCount + 63) / 64];
        _pendingSupportFaces = new ulong[supports * _changedFaces.Length];
        _faceNodes = topology.FaceNodes;
        Begin();
    }

    private static Topology GetTopology(ClothRig rig) => Topologies.GetValue(rig, static pattern => new Topology(pattern));

    internal ReadOnlySpan<Pair> Pairs => _pairs.AsSpan(0, _pairCount);

    internal bool EnsurePairs(ClothContactWorkspace work)
    {
        if (_pairsCurrent) return true;
        _pairCount = 0;
        _pairsCurrent = CollectPairs(work);
        return _pairsCurrent;
    }

    internal bool CollectSupport(int support, scoped in ClothBounds bounds, ClothContactWorkspace work,
        out ReadOnlySpan<int> faces)
    {
        int capacity = _rig.Triangles.Length;
        Span<int> storage = _supportFaces.AsSpan(support * capacity, capacity);
        Span<ulong> pending = _pendingSupportFaces.AsSpan(support * _changedFaces.Length, _changedFaces.Length);
        int count = _supportCounts[support];
        if (count < 0 || _queriedBounds[support] != bounds)
        {
            bool firstQuery = count < 0;
            count = 0;
            if (!CollectSupportNode(0, bounds, work, storage, ref count))
            {
                faces = default;
                return false;
            }

            storage[..count].Sort();
            pending.Clear();
            _queriedBounds[support] = bounds;
            if (firstQuery) _queriedSupports[_queriedCount++] = support;
        }
        else if (!UpdateSupport(bounds, work, storage, pending, ref count))
        {
            faces = default;
            return false;
        }

        _supportCounts[support] = count;
        faces = storage[..count];
        return true;
    }

    private bool UpdateSupport(in ClothBounds bounds, ClothContactWorkspace work, Span<int> faces,
        Span<ulong> pending, ref int count)
    {
        for (int word = 0; word < pending.Length; word++)
        {
            ulong changed = pending[word];
            while (changed != 0)
            {
                if (!work.Visit()) return false;
                int bit = BitOperations.TrailingZeroCount(changed);
                int face = word * 64 + bit;
                int index = faces[..count].BinarySearch(face);
                bool overlaps = _bounds[_faceNodes[face]].Overlaps(bounds);
                if (index < 0 && overlaps)
                {
                    index = ~index;
                    faces.Slice(index, count - index).CopyTo(faces[(index + 1)..]);
                    faces[index] = face;
                    count++;
                }
                else if (index >= 0 && !overlaps)
                {
                    faces.Slice(index + 1, count - index - 1).CopyTo(faces[index..]);
                    count--;
                }

                changed &= changed - 1;
            }
        }
        pending.Clear();
        return true;
    }

    private bool CollectSupportNode(int nodeIndex, in ClothBounds bounds, ClothContactWorkspace work,
        Span<int> faces, ref int count)
    {
        if (!work.Visit()) return false;
        if (!_bounds[nodeIndex].Overlaps(bounds)) return true;
        Node node = _nodes[nodeIndex];
        if (node.Leaf < 0)
            return CollectSupportNode(node.Left, bounds, work, faces, ref count) &&
                CollectSupportNode(node.Right, bounds, work, faces, ref count);

        faces[count++] = node.Leaf;
        return true;
    }

    internal bool IsCurrent => _built && _movedCount == 0 && !_workRefused;

    internal void Begin()
    {
        _built = false;
        _workRefused = false;
        _pairsCurrent = false;
        _proposedCoverage = null;
        _queriedCount = 0;
        Array.Fill(_supportCounts, -1);
        Array.Clear(_changedFaces);
        ClearMoves();
    }

    internal void KeepProposedCoverage(Vector3[] proposed) => _proposedCoverage = proposed;

    internal void RecordMove(int node, Vector3 point, ClothContactWorkspace work)
    {
        if (_built && !_workRefused)
            _workRefused = !CheckPosition(node, point, work);
    }

    private bool CheckPosition(int node, Vector3 point, ClothContactWorkspace work)
    {
        ulong bit = 1UL << (node % 64);
        int word = node / 64;
        if ((_movedPositions[word] & bit) != 0) return true;
        if (!work.Visit()) return false;
        if (Contains(_coverage[node], point)) return true;

        _movedPositions[word] |= bit;
        _movedCount++;
        int offset = node * _affectedBounds.Length;
        for (int i = 0; i < _affectedBounds.Length; i++)
            _affectedBounds[i] |= _positionDependencies[offset + i];
        return true;
    }

    private static bool Contains(in ClothBounds bounds, Vector3 point) =>
        point.X >= bounds.Min.X && point.Y >= bounds.Min.Y && point.Z >= bounds.Min.Z &&
        point.X <= bounds.Max.X && point.Y <= bounds.Max.Y && point.Z <= bounds.Max.Z;

    internal bool EnsureCoverage(Vector3[] opening, Vector3[] closing, float thickness, ClothContactWorkspace work,
        bool externallyChanged = false)
    {
        if (_workRefused) return false;
        if (_built && externallyChanged)
            for (int i = 0; i < closing.Length; i++)
                if (!CheckPosition(i, closing[i], work)) return false;
        if (IsCurrent) return true;

        bool refitted = _built
            ? RefitMoved(opening, closing, thickness, work)
            : RefitAll(opening, closing, thickness, work);
        if (!refitted) return false;

        ClearMoves();
        // Keep unaffected support memberships and update only refitted faces
        // when the owner next queries that support.
        for (int i = 0; i < _queriedCount; i++)
        {
            int offset = _queriedSupports[i] * _changedFaces.Length;
            for (int word = 0; word < _changedFaces.Length; word++)
                _pendingSupportFaces[offset + word] |= _changedFaces[word];
        }
        Array.Clear(_changedFaces);
        _pairsCurrent = false;
        _built = true;
        return true;
    }

    private bool RefitAll(Vector3[] opening, Vector3[] closing, float thickness, ClothContactWorkspace work)
    {
        for (int i = 0; i < closing.Length; i++)
            if (!RefitPosition(i, closing, work)) return false;
        for (int i = _nodes.Length - 1; i >= 0; i--)
            if (!RefitNode(i, opening, thickness, work)) return false;
        return true;
    }

    private bool RefitMoved(Vector3[] opening, Vector3[] closing, float thickness, ClothContactWorkspace work)
    {
        for (int word = 0; word < _movedPositions.Length; word++)
        {
            ulong pending = _movedPositions[word];
            while (pending != 0)
            {
                int bit = BitOperations.TrailingZeroCount(pending);
                if (!RefitPosition(word * 64 + bit, closing, work)) return false;
                pending &= pending - 1;
            }
        }
        // Node indices are in preorder, so children refit before parents.
        for (int word = _affectedBounds.Length - 1; word >= 0; word--)
        {
            ulong pending = _affectedBounds[word];
            while (pending != 0)
            {
                int bit = 63 - BitOperations.LeadingZeroCount(pending);
                if (!RefitNode(word * 64 + bit, opening, thickness, work)) return false;
                pending ^= 1UL << bit;
            }
        }
        return true;
    }

    private bool RefitPosition(int index, Vector3[] closing, ClothContactWorkspace work)
    {
        if (!work.Visit()) return false;
        Vector3 point = closing[index];
        ClothBounds bounds = new ClothBounds(point, point).Expand(ReusePadding);
        if (_proposedCoverage is { } proposed)
            bounds = ClothBounds.Enclose(bounds, new ClothBounds(proposed[index], proposed[index]));
        _coverage[index] = bounds;
        return true;
    }

    private bool RefitNode(int index, Vector3[] opening, float thickness, ClothContactWorkspace work)
    {
        if (!work.Visit()) return false;
        Node node = _nodes[index];
        if (node.Leaf >= 0)
        {
            _changedFaces[node.Leaf / 64] |= 1UL << (node.Leaf % 64);
            ClothTriangle face = _rig.Triangles[node.Leaf];
            ClothBounds bounds = new ClothFacet(face, opening).Bounds;
            bounds = ClothBounds.Enclose(bounds, _coverage[face.A]);
            bounds = ClothBounds.Enclose(bounds, _coverage[face.B]);
            _bounds[index] = ClothBounds.Enclose(bounds, _coverage[face.C]).Expand(thickness);
        }
        else
            _bounds[index] = ClothBounds.Enclose(_bounds[node.Left], _bounds[node.Right]);
        return true;
    }

    private void ClearMoves()
    {
        Array.Clear(_movedPositions);
        Array.Clear(_affectedBounds);
        _movedCount = 0;
    }

    private bool CollectPairs(ClothContactWorkspace work)
    {
        for (int i = 0; i < _selfTraversal.Length;)
        {
            if (!work.Visit()) return false;
            PairNode pair = _selfTraversal[i];
            if (!_bounds[pair.First].Overlaps(_bounds[pair.Last]))
            {
                i = pair.Skip;
                continue;
            }
            if (pair.Skip == i + 1)
                _pairs[_pairCount++] = new Pair(_nodes[pair.First].Leaf, _nodes[pair.Last].Leaf);
            i++;
        }
        return true;
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

    private sealed class Topology
    {
        internal readonly Node[] Nodes;
        internal readonly PairNode[] SelfTraversal;
        internal readonly ulong[] PositionDependencies;
        internal readonly int[] FaceNodes;

        internal Topology(ClothRig rig)
        {
            int faceCount = rig.Triangles.Length;
            Nodes = new Node[2 * faceCount - 1];
            var order = new int[faceCount];
            var center = new Vector3[faceCount];
            for (int i = 0; i < faceCount; i++)
            {
                order[i] = i;
                center[i] = rig.Triangles[i].Blend(rig.RestPositions, new Vector3(1f / 3));
            }
            int count = 0;
            Build(0, faceCount, order, center, -1, ref count);
            FaceNodes = new int[faceCount];
            PositionDependencies = BuildDependencies(rig);
            SelfTraversal = CompileSelfTraversal(faceCount, BuildExclusions(rig));
        }

        private ulong[] BuildDependencies(ClothRig rig)
        {
            int words = (Nodes.Length + 63) / 64;
            var dependencies = new ulong[rig.RestPositions.Length * words];
            for (int i = 0; i < Nodes.Length; i++)
            {
                int leaf = Nodes[i].Leaf;
                if (leaf < 0) continue;
                FaceNodes[leaf] = i;
                ClothTriangle face = rig.Triangles[leaf];
                for (int ancestor = i; ancestor >= 0; ancestor = Nodes[ancestor].Parent)
                {
                    ulong bit = 1UL << (ancestor % 64);
                    int word = ancestor / 64;
                    dependencies[face.A * words + word] |= bit;
                    dependencies[face.B * words + word] |= bit;
                    dependencies[face.C * words + word] |= bit;
                }
            }
            return dependencies;
        }

        // Emit eligible pairs in traversal order. A diagonal contributes
        // only its disjoint sibling pair and descendant sibling pairs.
        private PairNode[] CompileSelfTraversal(int faces, bool[] excluded)
        {
            int words = (faces + 63) / 64;
            var leafSets = new ulong[words * Nodes.Length];
            var exclusions = new ulong[leafSets.Length];
            BuildSets(faces, words, excluded, leafSets, exclusions);
            var output = new List<PairNode>();
            Emit(0, 0);
            return output.ToArray();

            void Emit(int a, int b)
            {
                if (AllNeighbours(a, b)) return;
                Node first = Nodes[a], last = Nodes[b];
                if (a == b)
                {
                    Emit(first.Left, first.Left);
                    Emit(first.Right, first.Right);
                    Emit(first.Left, first.Right);
                    return;
                }

                int index = output.Count;
                output.Add(default);
                if (first.Count > 1 || last.Count > 1)
                {
                    if (first.Count >= last.Count)
                    {
                        Emit(first.Left, b);
                        Emit(first.Right, b);
                    }
                    else
                    {
                        Emit(a, last.Left);
                        Emit(a, last.Right);
                    }
                }
                // Each emitted binary subtree covers unordered distinct
                // face pairs. At 256 faces the traversal has fewer than 65,536 entries.
                output[index] = new PairNode(checked((ushort)a), checked((ushort)b), checked((ushort)output.Count));
            }

            bool AllNeighbours(int a, int b)
            {
                for (int word = 0; word < words; word++)
                    if ((leafSets[b * words + word] & ~exclusions[a * words + word]) != 0)
                        return false;
                return true;
            }
        }

        private void BuildSets(int faces, int words, bool[] excluded, ulong[] leafSets, ulong[] exclusions)
        {
            for (int i = Nodes.Length - 1; i >= 0; i--)
            {
                Node node = Nodes[i];
                int offset = i * words;
                if (node.Leaf >= 0)
                {
                    leafSets[offset + node.Leaf / 64] = 1UL << (node.Leaf % 64);
                    for (int face = 0; face < faces; face++)
                        if (excluded[node.Leaf * faces + face])
                            exclusions[offset + face / 64] |= 1UL << (face % 64);
                    continue;
                }

                int left = node.Left * words, right = node.Right * words;
                for (int word = 0; word < words; word++)
                {
                    leafSets[offset + word] = leafSets[left + word] | leafSets[right + word];
                    exclusions[offset + word] = exclusions[left + word] & exclusions[right + word];
                }
            }
        }

        private int Build(int start, int length, int[] faces, Vector3[] centers, int parent, ref int next)
        {
            int index = next++;
            if (length == 1)
            {
                Nodes[index] = new Node(1, faces[start], -1, -1, parent);
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
            int left = Build(start, split, faces, centers, index, ref next);
            int right = Build(start + split, length - split, faces, centers, index, ref next);
            Nodes[index] = new Node(length, -1, left, right, parent);
            return index;
        }
    }

    internal readonly record struct Pair(int First, int Last);
    private readonly record struct PairNode(ushort First, ushort Last, ushort Skip);
    private readonly record struct Node(int Count, int Leaf, int Left, int Right, int Parent);
}
