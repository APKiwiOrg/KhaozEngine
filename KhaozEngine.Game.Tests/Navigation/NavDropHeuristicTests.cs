using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavDropHeuristicTests
{
    [Fact]
    public void PointAndRegionMatchDijkstraWhenLeavingAndReenteringTheGoalLayer()
    {
        var main = NavGrid.FromBlockedSurfaces(13, 1, 1, 0, 0, new bool[13],
            Enumerable.Range(0, 13).Select(x => x == 12 ? 0f : 4f).ToArray());
        var upper = NavGrid.FromBlockedSurfaces(1, 1, 1, 0, 0, new bool[1], new float[] { 4 });
        var lower = NavGrid.FromBlockedSurfaces(1, 1, 1, 1, 0, new bool[1], new float[] { 3 });
        NavLink[] links = { new(0, 0, 0, 1, 0, 0), new(1, 0, 0, 2, 0, 0) { Kind = NavLinkKind.Drop }, new(2, 0, 0, 0, 12, 0) };
        var space = new NavSpace(new[] { main, upper, lower }, links);
        var exits = new byte[13];
        exits[2] = 3;
        exits[1] = 2;
        for (int i = 3; i < 12; i++) exits[i] = 1;
        var graph = new NavTraversalGraph(space, 0, 1, new[] {
            new NavTraversalLayer(13, 1, Enumerable.Repeat(true, 13).ToArray(), exits),
            new NavTraversalLayer(1, 1, new[] { true }, new byte[1]),
            new NavTraversalLayer(1, 1, new[] { true }, new byte[1]),
        }, links);
        var planner = new GridPathPlanner(space, graph);
        Vector3 start = new(2.5f, 4, 0.5f), goal = new(12.5f, 0, 0.5f);
        NavPath point = planner.FindPath(start, goal, 0, PathQueryBudget.Default);
        NavPath region = planner.FindPath(start, new NavGoalRegion(goal, 0, p => p == goal), 0, PathQueryBudget.Default);
        // Independent weighted graph. The direct chain costs 10. The backward detour costs 2 + 1 + 2 + 1 = 6.
        var weighted = new List<(int From, int To, float Cost)> { (2, 1, 1), (1, 0, 1), (0, 13, 1), (13, 14, 2), (14, 12, 1) };
        for (int i = 2; i < 12; i++) weighted.Add((i, i + 1, 1));
        Assert.Equal(6, Dijkstra(weighted, 2, 12));
        Assert.Equal(NavPathStatus.Complete, point.Status);
        Assert.Equal(point.Waypoints, region.Waypoints);
        Assert.Equal(new[] { new NavWaypoint(new(1.5f, 0.5f), 0), new NavWaypoint(new(0.5f, 0.5f), 0),
            new NavWaypoint(new(0.5f, 0.5f), 1), new NavWaypoint(new(1.5f, 0.5f), 2) { Kind = NavWaypointKind.Drop },
            new NavWaypoint(new(12.5f, 0.5f), 0) }, point.Waypoints);
    }

    static float Dijkstra(List<(int From, int To, float Cost)> edges, int start, int target)
    {
        var distances = new Dictionary<int, float> { [start] = 0 };
        var pending = new PriorityQueue<int, float>();
        pending.Enqueue(start, 0);
        while (pending.TryDequeue(out int current, out float cost))
        {
            if (cost != distances[current]) continue;
            if (current == target) return cost;
            foreach (var edge in edges.Where(e => e.From == current))
            {
                float next = cost + edge.Cost;
                if (distances.TryGetValue(edge.To, out float old) && old <= next) continue;
                distances[edge.To] = next;
                pending.Enqueue(edge.To, next);
            }
        }
        return float.PositiveInfinity;
    }
}
