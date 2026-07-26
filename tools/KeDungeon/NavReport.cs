using System;
using System.Collections.Generic;
using KhaozEngine.Navigation;

namespace KeDungeon;

/// <summary>
/// Reports a baked <see cref="NavSpace"/>'s shape to the console for the ke-dungeon <c>nav</c> verb: per
/// floor its grid dimensions and passable/blocked cell counts, then the number of connected components
/// across the whole space. Dev tooling only: no localization requirements apply, output goes straight to
/// the console.
/// </summary>
public static class NavReport
{
    // Mirrors GridPathPlanner's NeighborDx/NeighborDz exactly: the four orthogonals first, then the four
    // diagonals, paired index-for-index. GridPathPlanner's own arrays (and the corner-cut rule in
    // UnionPassableNeighbors below) are private, so this is a deliberate copy kept in lockstep, not a
    // shared reference: a reported "1 component" is only meaningful if it means the planner can actually
    // reach every passable cell from every other one, not just that they look adjacent on the grid.
    static readonly int[] NeighborDx = { 1, -1, 0, 0, 1, 1, -1, -1 };
    static readonly int[] NeighborDz = { 0, 0, 1, -1, 1, -1, 1, -1 };

    /// <summary>
    /// Prints <paramref name="space"/>'s per-floor grid dimensions and passable/blocked cell counts
    /// (<c>floor {f}: {width}x{height} grid, passable {n}, blocked {n}</c>), then its total
    /// connected-component count (<c>components: {n}</c>), and returns that count.
    /// <para>
    /// A cell counts as passable via <see cref="NavGrid.IsPassable"/> at radius 0, the same method
    /// <see cref="GridPathPlanner"/> checks (through its own <c>Blocks</c> helper), at the smallest
    /// possible agent: this verb takes no --agent-radius option, so zero is the only notion of "fits"
    /// available. Components are computed over passable cells only: a blocked cell contributes to its
    /// floor's blocked count but is never a node. Two passable cells in the same layer join when they are
    /// 8-neighbor adjacent with corner-cutting prevented exactly like <see cref="GridPathPlanner"/>'s A*
    /// neighbor expansion (a diagonal step only counts when both orthogonal companion cells are also
    /// passable), and two passable cells in any layers join when a <see cref="NavSpace.Links"/> entry
    /// connects them and both its endpoints are passable, addressed with the same flat node id
    /// (<c>layerOffset[layer] + z * layer.Width + x</c>) <see cref="GridPathPlanner"/>'s constructor uses
    /// to turn links into graph edges, so a stair link merges its two floors into one component exactly as
    /// the planner would actually cross it.
    /// </para>
    /// </summary>
    public static int Print(NavSpace space)
    {
        ArgumentNullException.ThrowIfNull(space);

        IReadOnlyList<NavGrid> layers = space.Layers;
        var layerOffset = new int[layers.Count];
        int totalNodes = 0;
        for (int i = 0; i < layers.Count; i++)
        {
            layerOffset[i] = totalNodes;
            totalNodes += layers[i].Width * layers[i].Height;
        }

        var components = new UnionFind(totalNodes);

        for (int f = 0; f < layers.Count; f++)
        {
            NavGrid grid = layers[f];
            int passableCount = UnionPassableNeighbors(grid, layerOffset[f], components);
            int blockedCount = grid.Width * grid.Height - passableCount;
            Console.WriteLine(
                $"floor {f}: {grid.Width}x{grid.Height} grid, passable {passableCount}, blocked {blockedCount}");
        }

        foreach (NavLink link in space.Links)
        {
            NavGrid fromGrid = layers[link.FromLayer];
            NavGrid toGrid = layers[link.ToLayer];
            if (!IsPassable(fromGrid, link.FromX, link.FromZ) || !IsPassable(toGrid, link.ToX, link.ToZ))
            {
                continue;
            }

            int fromId = layerOffset[link.FromLayer] + link.FromZ * fromGrid.Width + link.FromX;
            int toId = layerOffset[link.ToLayer] + link.ToZ * toGrid.Width + link.ToX;
            components.Union(fromId, toId);
        }

        int componentCount = CountComponents(layers, layerOffset, components);
        Console.WriteLine($"components: {componentCount}");
        return componentCount;
    }

    // Unions every passable cell in grid with its passable 8-neighbors (corner-cut prevented, see the
    // type doc), and returns the floor's passable cell count.
    static int UnionPassableNeighbors(NavGrid grid, int baseId, UnionFind components)
    {
        int passableCount = 0;
        for (int z = 0; z < grid.Height; z++)
        {
            for (int x = 0; x < grid.Width; x++)
            {
                if (!IsPassable(grid, x, z))
                {
                    continue;
                }

                passableCount++;
                int nodeId = baseId + z * grid.Width + x;

                for (int n = 0; n < NeighborDx.Length; n++)
                {
                    int nx = x + NeighborDx[n];
                    int nz = z + NeighborDz[n];
                    if (!grid.InBounds(nx, nz) || !IsPassable(grid, nx, nz))
                    {
                        continue;
                    }

                    bool diagonal = n >= 4;
                    if (diagonal && (!IsPassable(grid, nx, z) || !IsPassable(grid, x, nz)))
                    {
                        continue; // Corner-cut prevention: both orthogonal companions must be passable.
                    }

                    components.Union(nodeId, baseId + nz * grid.Width + nx);
                }
            }
        }

        return passableCount;
    }

    static int CountComponents(IReadOnlyList<NavGrid> layers, int[] layerOffset, UnionFind components)
    {
        var roots = new HashSet<int>();
        for (int f = 0; f < layers.Count; f++)
        {
            NavGrid grid = layers[f];
            for (int z = 0; z < grid.Height; z++)
            {
                for (int x = 0; x < grid.Width; x++)
                {
                    if (!IsPassable(grid, x, z))
                    {
                        continue;
                    }

                    roots.Add(components.Find(layerOffset[f] + z * grid.Width + x));
                }
            }
        }

        return roots.Count;
    }

    static bool IsPassable(NavGrid grid, int cx, int cz) => grid.IsPassable(cx, cz, agentRadius: 0f);

    // Standard union-find (disjoint set) over the NavSpace's flat node-id space, used only to count
    // connected components. Path-halving find, union by rank.
    sealed class UnionFind
    {
        readonly int[] _parent;
        readonly byte[] _rank;

        public UnionFind(int count)
        {
            _parent = new int[count];
            _rank = new byte[count];
            for (int i = 0; i < count; i++)
            {
                _parent[i] = i;
            }
        }

        public int Find(int x)
        {
            while (_parent[x] != x)
            {
                _parent[x] = _parent[_parent[x]];
                x = _parent[x];
            }

            return x;
        }

        public void Union(int a, int b)
        {
            int rootA = Find(a);
            int rootB = Find(b);
            if (rootA == rootB)
            {
                return;
            }

            if (_rank[rootA] < _rank[rootB])
            {
                (rootA, rootB) = (rootB, rootA);
            }

            _parent[rootB] = rootA;
            if (_rank[rootA] == _rank[rootB])
            {
                _rank[rootA]++;
            }
        }
    }
}
