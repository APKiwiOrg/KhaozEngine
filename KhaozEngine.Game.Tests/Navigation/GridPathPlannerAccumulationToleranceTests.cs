using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Navigation;

public class GridPathPlannerAccumulationToleranceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtremePrefixCompleteCostStaysWithinTheStatedAccumulationBound(bool region)
    {
        // #1712 F4: 1 mm increments after a 2e12 m prefix are quantized by the prefix's ULP.
        // This scale is outside the documented absolute 0.00001 m range. Do not pin a suboptimal choice.
        const float big = 2e12f, cell = 0.001f, knob = 13.03f;
        const int target = 13;
        NavGrid large = NavGrid.FromBlockedSurfaces(1, 1, big, -big / 2f, -big / 2f,
            new bool[1], new[] { 10f });
        NavGrid small = NavGrid.FromBlockedSurfaces(target + 1, 1, cell, 0f, 0f,
            new bool[target + 1], new float[target + 1]);
        var stair = new NavLink(0, 0, 0, 1, 0, 0);
        var hop = new NavLink(1, 0, 0, 1, target, 0) { Kind = NavLinkKind.Hop };
        var space = new NavSpace(new[] { large, small }, new[] { stair, hop });
        var exits = new byte[target + 1];
        for (int x = 0; x < target; x++) exits[x] = 1;
        var graph = new NavTraversalGraph(space, 0f, 1f, new[]
        {
            new NavTraversalLayer(1, 1, new[] { true }, new byte[1]),
            new NavTraversalLayer(target + 1, 1, Enumerable.Repeat(true, target + 1).ToArray(), exits),
        }, new[] { stair, hop });
        var planner = new GridPathPlanner(space, graph, knob);
        Vector2 centre = small.CellCenter(target, 0);
        Vector3 start = new(0f, 10f, 0f), goal = new(centre.X, 0f, centre.Y);
        var budget = new PathQueryBudget { MaxExpandedNodes = int.MaxValue, SnapRadius = 0f };
        NavPath path = region
            ? planner.FindPath(start, new NavGoalRegion(goal, 0f, p => p == goal), 0f, budget)
            : planner.FindPath(start, goal, 0f, budget);

        var edges = new List<(int From, int To, float Price)> { (0, 1, big), (1, target + 1, cell * knob) };
        for (int x = 0; x < target; x++) edges.Add((x + 1, x + 2, cell));
        double optimum = Dijkstra(edges, target + 1);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.NotEmpty(path.Waypoints);
        int prior = 0;
        double cost = 0d;
        foreach (NavWaypoint waypoint in path.Waypoints)
        {
            int at = waypoint.Layer == 0 ? 0 : small.CellOf(waypoint.Position.X, waypoint.Position.Y).X + 1;
            if (at == prior) continue;
            cost += edges.Where(e => e.From == prior && e.To == at).Min(e => e.Price);
            prior = at;
        }
        Assert.Equal(target + 1, prior);
        const int edgeBound = target + 1;
        const double envelope = 2e12;
        double ulp = Math.BitIncrement(envelope) - envelope;
        double numericBound = 132d * edgeBound * ulp;
        output.WriteLine($"Cost {cost:R}, oracle {optimum:R}, gap {cost - optimum:R}, ULP {ulp:R}, bound {numericBound:R}");
        Assert.True(numericBound > 0.00001d);
        Assert.InRange(cost - optimum, 0d, numericBound);
    }

    static double Dijkstra(List<(int From, int To, float Price)> edges, int goal)
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
                double next = cost + edge.Price;
                if (costs.TryGetValue(edge.To, out double old) && next >= old) continue;
                costs[edge.To] = next;
                pending.Enqueue(edge.To, next);
            }
        }
        return double.PositiveInfinity;
    }
}
