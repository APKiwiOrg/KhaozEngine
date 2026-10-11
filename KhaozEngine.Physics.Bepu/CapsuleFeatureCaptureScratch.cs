using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Physics.Bepu;

/// <summary>Reusable storage for capturing and admitting polyhedron leaves, owned by one query scratch and used
/// under its owner's query monitor. Arrays are handed out at exact lengths, because admission and its consumers
/// read lengths, and stay valid until <see cref="Reset"/>. A warm query of the same scene rents the same arrays in
/// the same order and allocates nothing. The leaves it captures carry faces and normals but no edge or vertex
/// incidence, which only the feature query reads.</summary>
internal sealed class CapsuleFeatureCaptureScratch
{
    readonly ExactLengthArrays<Vector3> _locals = new();
    readonly ExactLengthArrays<FeaturePoint> _points = new();
    readonly ExactLengthArrays<int> _indices = new();
    readonly ExactLengthArrays<int[]> _faces = new();
    readonly List<CapsuleFeaturePolyhedron> _leaves = [];
    int _leavesUsed;

    internal List<Vector3> HullVertices { get; } = [];
    internal Dictionary<int, int> HullIds { get; } = [];
    internal HashSet<Vector3> Positions { get; } = [];
    internal HashSet<int> FaceVertices { get; } = [];
    internal List<CapsuleFeaturePolyhedronEdge> Edges { get; } = [];
    internal Dictionary<(int, int), int> EdgeIds { get; } = [];
    internal List<List<int>> Incidence { get; } = [];
    internal List<int> AllFaces { get; } = [];
    internal HashSet<int> Reached { get; } = [];
    internal Queue<int> Pending { get; } = new();

    internal void Reset()
    {
        _locals.Reset();
        _points.Reset();
        _indices.Reset();
        _faces.Reset();
        _leavesUsed = 0;
    }

    internal Vector3[] Locals(int length) => _locals.Rent(length);
    internal FeaturePoint[] Points(int length) => _points.Rent(length);
    internal int[] Indices(int length) => _indices.Rent(length);
    internal int[][] Faces(int length) => _faces.Rent(length);

    internal CapsuleFeaturePolyhedron Leaf()
    {
        if (_leavesUsed == _leaves.Count) _leaves.Add(new CapsuleFeaturePolyhedron());
        return _leaves[_leavesUsed++];
    }

    /// <summary>The incidence list of <paramref name="vertex"/>, cleared, growing the reused lists when needed.</summary>
    internal List<int> ClearedIncidence(int vertex)
    {
        while (Incidence.Count <= vertex) Incidence.Add([]);
        Incidence[vertex].Clear();
        return Incidence[vertex];
    }
}

/// <summary>Arrays of exact lengths, rented in order and all returned together.</summary>
internal sealed class ExactLengthArrays<T>
{
    readonly Dictionary<int, List<T[]>> _arrays = [];
    readonly Dictionary<int, int> _used = [];

    internal T[] Rent(int length)
    {
        if (!_arrays.TryGetValue(length, out List<T[]>? arrays))
        {
            arrays = [];
            _arrays.Add(length, arrays);
        }
        _used.TryGetValue(length, out int used);
        if (used == arrays.Count) arrays.Add(new T[length]);
        _used[length] = used + 1;
        return arrays[used];
    }

    internal void Reset()
    {
        foreach (int length in _arrays.Keys) _used[length] = 0;
    }
}
