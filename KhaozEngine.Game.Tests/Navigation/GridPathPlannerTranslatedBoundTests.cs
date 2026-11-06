using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class GridPathPlannerTranslatedBoundTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    public void TranslatedPointAndRegionCostsEqualTheThreeEdgeOptimum(int frame, bool region)
    {
        // #1712 F1: rounded world centres cannot price a region bound scaled in local cell units.
        bool diagonal = frame is 2 or 3;
        bool alongZ = frame == 1;
        float originX = frame == 1 ? 0f : frame is 3 or 4 ? -1000000f : 1000000f;
        float originZ = frame is 1 or 2 or 5 or 6 ? 1000000f : frame == 3 ? -1000000f : 0f;
        float yaw = frame is 5 or 6 ? MathF.PI / 2f : 0f;
        int width = alongZ ? 1 : 4, height = diagonal || alongZ ? 4 : 1;
        NavGrid grid = NavGrid.FromBlockedSurfaces(width, height, 0.1f, originX, originZ,
            new bool[width * height], new float[width * height], yawRadians: yaw);
        var hop = new NavLink(0, 0, 0, 0, alongZ ? 0 : 3, diagonal || alongZ ? 3 : 0)
        { Kind = NavLinkKind.Hop };
        var grids = new List<NavGrid> { grid };
        if (frame == 6)
            grids.Add(NavGrid.FromBlockedSurfaces(1, 1, 0.1f, 2000000f, -2000000f,
                new bool[1], new[] { 10f }, yawRadians: 0.37f));
        var space = new NavSpace(grids, new[] { hop });
        var layers = grids.Select(g => new NavTraversalLayer(g.Width, g.Height,
            Enumerable.Repeat(true, g.Width * g.Height).ToArray(), Exits(g))).ToArray();
        var graph = new NavTraversalGraph(space, 0f, 1f, layers, new[] { hop });
        float hopCostCells = diagonal ? 4.3f : 3.05f;
        var planner = new GridPathPlanner(space, graph, hopCostCells);
        Vector2 a = grid.CellCenter(0, 0), b = grid.CellCenter(hop.ToX, hop.ToZ);
        Vector3 start = new(a.X, 0f, a.Y), goal = new(b.X, 0f, b.Y);
        var budget = new PathQueryBudget { MaxExpandedNodes = int.MaxValue, SnapRadius = 0f };
        NavPath path = region
            ? planner.FindPath(start, new NavGoalRegion(goal, 0f, feet => feet == goal), 0f, budget)
            : planner.FindPath(start, goal, 0f, budget);

        float optimum = Dijkstra(grid, hop, hopCostCells);
        float step = diagonal ? 0.1f * MathF.Sqrt(2f) : 0.1f;
        Assert.Equal((step + step) + step, optimum);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.True(MathF.Abs(PathCost(grid, hop, hopCostCells, path) - optimum) <= 0.00001f);
        Assert.Equal(3, path.Waypoints.Count);
        Assert.All(path.Waypoints, waypoint => Assert.Equal(NavWaypointKind.Walk, waypoint.Kind));
    }

    static byte[] Exits(NavGrid grid)
    {
        var exits = new byte[grid.Width * grid.Height];
        for (int z = 0; z < grid.Height; z++)
            for (int x = 0; x < grid.Width; x++)
                for (int i = 0; i < 8; i++)
                    if (grid.InBounds(x + Dx[i], z + Dz[i])) exits[z * grid.Width + x] |= (byte)(1 << i);
        return exits;
    }

    static readonly int[] Dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
    static readonly int[] Dz = { 0, 0, 1, -1, 1, -1, 1, -1 };

    static float Dijkstra(NavGrid grid, NavLink hop, float hopCostCells)
    {
        var costs = new Dictionary<(int X, int Z), float> { [(0, 0)] = 0f };
        var pending = new PriorityQueue<(int X, int Z), float>();
        pending.Enqueue((0, 0), 0f);
        while (pending.TryDequeue(out var node, out float cost))
        {
            if (cost != costs[node]) continue;
            if (node == (hop.ToX, hop.ToZ)) return cost;
            for (int i = 0; i < 8; i++)
            {
                var next = (X: node.X + Dx[i], Z: node.Z + Dz[i]);
                if (grid.InBounds(next.X, next.Z)) Relax(next, grid.CellSize * (i < 4 ? 1f : MathF.Sqrt(2f)));
            }
            if (node == (0, 0)) Relax((hop.ToX, hop.ToZ), grid.CellSize * hopCostCells);
            void Relax((int X, int Z) next, float edge)
            {
                float candidate = cost + edge;
                if (costs.TryGetValue(next, out float old) && candidate >= old) return;
                costs[next] = candidate;
                pending.Enqueue(next, candidate);
            }
        }
        return float.PositiveInfinity;
    }

    static float PathCost(NavGrid grid, NavLink hop, float hopCostCells, NavPath path)
    {
        int x = 0, z = 0;
        float cost = 0f;
        foreach (NavWaypoint waypoint in path.Waypoints)
        {
            int nx = -1, nz = -1;
            for (int cz = 0; cz < grid.Height; cz++)
                for (int cx = 0; cx < grid.Width; cx++)
                    if (grid.CellCenter(cx, cz) == waypoint.Position) { nx = cx; nz = cz; }
            Assert.True(nx >= 0 && nz >= 0);
            if (nx == x && nz == z) continue;
            if (waypoint.Kind == NavWaypointKind.Hop)
            {
                Assert.Equal((0, 0, hop.ToX, hop.ToZ), (x, z, nx, nz));
                cost += grid.CellSize * hopCostCells;
            }
            else
            {
                Assert.InRange(Math.Abs(nx - x), 0, 1);
                Assert.InRange(Math.Abs(nz - z), 0, 1);
                cost += grid.CellSize * (nx != x && nz != z ? MathF.Sqrt(2f) : 1f);
            }
            x = nx;
            z = nz;
        }
        return cost;
    }
}
