using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class GridPathPlannerNumericCostTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    public void LongRoutesUseTheDoubleSumOfTheirActualEdgePrices(int sample, bool region)
    {
        // #1712 F2: float octile and repeated float addition disagree about the cheaper admitted route.
        float cell = sample == 0 ? 10000f : 0.1f;
        int target = sample == 0 ? 34 : 1000, width = target + 1;
        NavGrid grid = NavGrid.FromBlockedSurfaces(width, 2, cell, 0f, 0f,
            new bool[width * 2], new float[width * 2]);
        var exits = new byte[width * 2];
        for (int x = 0; x < target - 1; x++) exits[x] = 1;
        exits[target - 1] = 16;
        var hop = new NavLink(0, 0, 0, 0, target, 1) { Kind = NavLinkKind.Hop };
        float hopCells = (target - 1) + MathF.Sqrt(2f);
        if (sample == 2) hopCells = MathF.BitIncrement(hopCells);
        var space = NavSpaceWith(hop);
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(width, 2, Enumerable.Repeat(true, width * 2).ToArray(), exits) },
            new[] { hop });
        var planner = new GridPathPlanner(space, graph, hopCells);
        Vector2 a = grid.CellCenter(0, 0), b = grid.CellCenter(target, 1);
        Vector3 from = new(a.X, 0f, a.Y), goal = new(b.X, 0f, b.Y);
        var budget = new PathQueryBudget { MaxExpandedNodes = int.MaxValue, SnapRadius = 0f };
        NavPath path = region
            ? planner.FindPath(from, new NavGoalRegion(goal, 0f, p => p == goal), 0f, budget)
            : planner.FindPath(from, goal, 0f, budget);

        // Only the row's directed chain and the direct Hop reach the goal. The oracle has no heuristic.
        var edges = new List<(int From, int To, float Cost)>();
        for (int x = 0; x < target - 1; x++) edges.Add((x, x + 1, cell));
        float diagonal = cell * MathF.Sqrt(2f), hopPrice = cell * hopCells;
        int end = width + target;
        edges.Add((target - 1, end, diagonal));
        edges.Add((0, end, hopPrice));
        double optimum = Dijkstra(edges, end);
        double walking = (target - 1) * (double)cell + diagonal;
        bool hopOptimal = hopPrice < walking;
        Assert.Equal(sample == 1, hopOptimal);
        Assert.Equal(hopOptimal ? (double)hopPrice : walking, optimum);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        bool selectedHop = path.Waypoints.Any(p => p.Kind == NavWaypointKind.Hop);
        Assert.Equal(hopOptimal, selectedHop);
        Assert.Equal(selectedHop ? 2 : target, path.Waypoints.Count);
        double actual = selectedHop ? hopPrice : walking;
        Assert.Equal(optimum, actual);

        NavSpace NavSpaceWith(NavLink link) => new(new[] { grid }, new[] { link });
    }

    static double Dijkstra(List<(int From, int To, float Cost)> edges, int goal)
    {
        var costs = new Dictionary<int, double> { [0] = 0d };
        var pending = new PriorityQueue<int, double>();
        pending.Enqueue(0, 0d);
        while (pending.TryDequeue(out int at, out double cost))
        {
            if (cost != costs[at]) continue;
            if (at == goal) return cost;
            foreach (var edge in edges.Where(e => e.From == at))
            {
                double candidate = cost + edge.Cost;
                if (costs.TryGetValue(edge.To, out double old) && candidate >= old) continue;
                costs[edge.To] = candidate;
                pending.Enqueue(edge.To, candidate);
            }
        }
        return double.PositiveInfinity;
    }
}
