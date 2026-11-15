using System;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Navigation;

/// <summary>
/// Immutable weak components of a query-edge superset. Equal labels never prove directed reachability.
/// Shared eagerly by every planner over the same immutable source graph, regardless of query restrictions.
/// </summary>
internal sealed class PlannerConnectivity
{
    static readonly ConditionalWeakTable<NavSpace, Lazy<PlannerConnectivity>> Spaces = new();
    static readonly ConditionalWeakTable<NavTraversalGraph, Lazy<PlannerConnectivity>> Graphs = new();

    readonly int[] _labels;
    readonly int[] _offsets;
    readonly int[] _members;

    internal static PlannerConnectivity For(NavSpace space, NavTraversalGraph? traversal)
        => traversal is null
            ? Spaces.GetValue(space, static key => new(() => new(key, null))).Value
            : Graphs.GetValue(traversal, static key => new(() => new(key.Space, key))).Value;

    internal PlannerConnectivity(NavSpace space, NavTraversalGraph? traversal)
    {
        var layerOffsets = new int[space.Layers.Count];
        int total = 0;
        for (int layer = 0; layer < layerOffsets.Length; layer++)
        {
            layerOffsets[layer] = total;
            total += space.Layers[layer].Width * space.Layers[layer].Height;
        }

        _labels = new int[total];
        Array.Fill(_labels, -1);
        // Union sizes become root-to-component ids, then member insertion cursors. No retained rank array.
        var sizes = new int[total];
        int passable = 0;
        for (int layer = 0; layer < layerOffsets.Length; layer++)
        {
            NavGrid grid = space.Layers[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    if (!grid.IsPassable(x, z, 0f) || traversal is not null && !traversal.IsNodePassable(layer, x, z)) continue;
                    int id = layerOffsets[layer] + z * grid.Width + x;
                    _labels[id] = id;
                    sizes[id] = 1;
                    passable++;
                }
        }

        for (int layer = 0; layer < layerOffsets.Length; layer++)
        {
            NavGrid grid = space.Layers[layer];
            int offset = layerOffsets[layer];
            for (int z = 0; z < grid.Height; z++)
                for (int x = 0; x < grid.Width; x++)
                {
                    int from = offset + z * grid.Width + x;
                    if (_labels[from] < 0) continue;
                    for (int direction = 0; direction < 8; direction++)
                    {
                        // Include all eight directed exits. Query radius, corner companions and captured
                        // restrictions may remove an edge, but must never add one outside these labels.
                        if (traversal is not null && (traversal.Layers[layer].ExitMask(x, z) & (1 << direction)) == 0) continue;
                        int nx = x + NavTraversalLayer.NeighborX(direction);
                        int nz = z + NavTraversalLayer.NeighborZ(direction);
                        if (grid.InBounds(nx, nz)) Union(from, offset + nz * grid.Width + nx, sizes);
                    }
                }
        }
        foreach (NavLink link in traversal?.Links ?? space.Links)
        {
            // Include Stair, Hop and Drop even for WalkOnly and profile-restricted planners.
            int from = layerOffsets[link.FromLayer] + link.FromZ * space.Layers[link.FromLayer].Width + link.FromX;
            int to = layerOffsets[link.ToLayer] + link.ToZ * space.Layers[link.ToLayer].Width + link.ToX;
            Union(from, to, sizes);
        }

        // Freeze roots before reusing sizes. Component members retain canonical flat-node order.
        for (int id = 0; id < total; id++)
            if (_labels[id] >= 0) _labels[id] = Root(id);
        int components = 0;
        for (int id = 0; id < total; id++)
            if (_labels[id] == id) sizes[id] = components++;
        _offsets = new int[components + 1];
        for (int id = 0; id < total; id++)
        {
            if (_labels[id] < 0) continue;
            _labels[id] = sizes[_labels[id]];
            _offsets[_labels[id] + 1]++;
        }
        for (int component = 0; component < components; component++)
            _offsets[component + 1] += _offsets[component];
        Array.Copy(_offsets, sizes, components);
        _members = new int[passable];
        for (int id = 0; id < total; id++)
            if (_labels[id] >= 0) _members[sizes[_labels[id]]++] = id;
    }

    internal int ComponentOf(int id) => (uint)id < (uint)_labels.Length ? _labels[id] : -1;

    internal ReadOnlySpan<int> Members(int component)
        => _members.AsSpan(_offsets[component], _offsets[component + 1] - _offsets[component]);

    internal long StorageBytesForTests => sizeof(int) * ((long)_labels.Length + _offsets.Length + _members.Length);

    int Root(int id)
    {
        while (_labels[id] != id)
        {
            _labels[id] = _labels[_labels[id]];
            id = _labels[id];
        }
        return id;
    }

    void Union(int from, int to, int[] sizes)
    {
        if (_labels[from] < 0 || _labels[to] < 0) return;
        int first = Root(from), second = Root(to);
        if (first == second) return;
        if (sizes[first] < sizes[second]) (first, second) = (second, first);
        _labels[second] = first;
        sizes[first] += sizes[second];
    }
}
