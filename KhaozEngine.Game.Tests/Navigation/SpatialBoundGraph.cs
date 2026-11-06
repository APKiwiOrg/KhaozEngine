using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Navigation;

/// <summary>Small directed graphs with independently recorded weighted edges for exhaustive shortest paths.</summary>
internal sealed class SpatialBoundGraph
{
    internal readonly record struct Node(int Layer, int X, int Z);
    internal readonly record struct Edge(Node From, Node To, float Cost, NavWaypointKind Kind);
    internal NavSpace Space { get; }
    internal NavTraversalGraph Traversal { get; }
    internal List<Edge> Edges { get; } = new();
    internal const int Side = 8;

    internal SpatialBoundGraph(int seed, bool differentFrames)
    {
        var random = new Random(seed);
        var grids = new NavGrid[3];
        var layers = new NavTraversalLayer[3];
        int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] dz = { 0, 0, 1, -1, 1, -1, 1, -1 };
        for (int layer = 0; layer < grids.Length; layer++)
        {
            float cell = differentFrames && layer == 1 ? 0.5f : 1f;
            float yaw = differentFrames ? layer * 0.37f : 0f;
            float origin = differentFrames ? layer * 1.25f : 0f;
            float[] heights = Enumerable.Range(0, Side * Side).Select(i => layer * 8f + i % Side * 0.25f).ToArray();
            grids[layer] = NavGrid.FromBlockedSurfaces(Side, Side, cell, origin, -origin,
                new bool[heights.Length], heights, yawRadians: yaw);
            var exits = new byte[heights.Length];
            for (int z = 0; z < Side; z++)
                for (int x = 0; x < Side; x++)
                    for (int i = 0; i < dx.Length; i++)
                    {
                        int nx = x + dx[i], nz = z + dz[i];
                        if (nx < 0 || nx >= Side || nz < 0 || nz >= Side || random.NextDouble() < 0.15) continue;
                        exits[z * Side + x] |= (byte)(1 << i);
                        Edges.Add(new(new(layer, x, z), new(layer, nx, nz),
                            i < 4 ? cell : cell * MathF.Sqrt(2f), NavWaypointKind.Walk));
                    }
            layers[layer] = new NavTraversalLayer(Side, Side, Enumerable.Repeat(true, heights.Length).ToArray(), exits);
        }
        var links = new List<NavLink>();
        for (int i = 0; i < 24; i++)
        {
            int from = random.Next(3), to = (from + 1 + random.Next(2)) % 3;
            Add(new NavLink(from, random.Next(Side), random.Next(Side), to, random.Next(Side), random.Next(Side)));
        }
        // Same-layer Drops and vertical links exercise zero span separately from cheap spatial shortcuts.
        for (int layer = 0; layer < 3; layer++)
            Add(new NavLink(layer, 6, 3, layer, 1, 3) { Kind = NavLinkKind.Drop });
        Add(new NavLink(2, 0, 0, 0, 0, 0) { Kind = NavLinkKind.Drop });
        Space = new NavSpace(grids, links);
        Traversal = new NavTraversalGraph(Space, 0f, 1f, layers, links);

        void Add(NavLink link)
        {
            links.Add(link);
            NavGrid source = grids[link.FromLayer], target = grids[link.ToLayer];
            float cost = source.CellSize;
            if (link.Kind == NavLinkKind.Drop)
            {
                Vector2 a = source.CellCenter(link.FromX, link.FromZ), b = target.CellCenter(link.ToX, link.ToZ);
                double x = Math.Abs((double)a.X - b.X), z = Math.Abs((double)a.Y - b.Y);
                double fall = source.RestingHeightAt(link.FromX, link.FromZ)!.Value -
                    (double)target.RestingHeightAt(link.ToX, link.ToZ)!.Value;
                cost = (float)(Math.Max(x, z) + (MathF.Sqrt(2f) - 1d) * Math.Min(x, z) + fall);
            }
            Edges.Add(new(new(link.FromLayer, link.FromX, link.FromZ), new(link.ToLayer, link.ToX, link.ToZ),
                cost, link.Kind == NavLinkKind.Drop ? NavWaypointKind.Drop : NavWaypointKind.Walk));
        }
    }

    internal Vector3 Feet(Node node)
    {
        NavGrid grid = Space.Layers[node.Layer];
        Vector2 xz = grid.CellCenter(node.X, node.Z);
        return new(xz.X, grid.RestingHeightAt(node.X, node.Z)!.Value, xz.Y);
    }

    internal float ShortestCost(Node start, Node goal)
    {
        // Exhaustive Dijkstra has no spatial estimate and never stops at a budget frontier.
        var distance = new Dictionary<Node, float> { [start] = 0f };
        var queue = new PriorityQueue<Node, float>();
        queue.Enqueue(start, 0f);
        while (queue.TryDequeue(out Node current, out float cost))
        {
            if (cost != distance[current]) continue;
            if (current == goal) return cost;
            foreach (Edge edge in Edges.Where(e => e.From == current))
            {
                float next = cost + edge.Cost;
                if (distance.TryGetValue(edge.To, out float old) && next >= old) continue;
                distance[edge.To] = next;
                queue.Enqueue(edge.To, next);
            }
        }
        return float.PositiveInfinity;
    }

    internal float PathCost(Node start, NavPath path)
    {
        float cost = 0f;
        Node previous = start;
        foreach (NavWaypoint waypoint in path.Waypoints)
        {
            (int x, int z) = Space.Layers[waypoint.Layer].CellOf(waypoint.Position.X, waypoint.Position.Y);
            var next = new Node(waypoint.Layer, x, z);
            if (previous == next) continue;
            cost += Edges.Where(e => e.From == previous && e.To == next && e.Kind == waypoint.Kind).Min(e => e.Cost);
            previous = next;
        }
        return cost;
    }
}
