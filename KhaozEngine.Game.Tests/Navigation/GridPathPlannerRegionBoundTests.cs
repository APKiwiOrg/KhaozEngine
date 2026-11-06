using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class GridPathPlannerRegionBoundTests
{
    [Fact]
    public void SquareCornerMemberKeepsTheCheapestCompleteRouteWithoutLinks()
    {
        // #1712: the corner costs one step, but distance to the inscribed circle charges it
        // a positive heuristic and lets the more expensive diagonal member finish first.
        NavGrid grid = NavGrid.FromBlockedSurfaces(5, 5, 1f, 0f, 0f, new bool[25], new float[25]);
        NavSpace space = NavSpace.Single(grid);
        Vector3 start = new(1.5f, 0f, 0.5f);
        var region = new NavGoalRegion(new(4.5f, 0f, 4.5f), 4f,
            feet => feet == new Vector3(0.5f, 0f, 0.5f) || feet == new Vector3(2.5f, 0f, 1.5f));
        float optimum = Dijkstra(grid, region, (1, 0));
        Assert.Equal(1f, optimum);

        NavPath reference = new ReferenceGridPathPlanner(space).FindPath(start, region, 0f, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, reference.Status);
        Assert.Equal(optimum, PathCost(reference));

        NavPath path = new GridPathPlanner(space).FindPath(start, region, 0f, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(optimum, PathCost(path));

        float PathCost(NavPath route)
        {
            Vector2 previous = new(start.X, start.Z);
            float cost = 0f;
            foreach (NavWaypoint waypoint in route.Waypoints)
            {
                cost += Vector2.Distance(previous, waypoint.Position);
                previous = waypoint.Position;
            }
            return cost;
        }
    }

    static float Dijkstra(NavGrid grid, NavGoalRegion region, (int X, int Z) start)
    {
        var distance = new Dictionary<(int X, int Z), float> { [start] = 0f };
        var queue = new PriorityQueue<(int X, int Z), float>();
        queue.Enqueue(start, 0f);
        while (queue.TryDequeue(out var cell, out float cost))
        {
            if (cost != distance[cell]) continue;
            Vector2 centre = grid.CellCenter(cell.X, cell.Z);
            if (region.Contains(new(centre.X, 0f, centre.Y))) return cost;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    var next = (X: cell.X + dx, Z: cell.Z + dz);
                    if (!grid.IsPassable(next.X, next.Z, 0f) ||
                        dx != 0 && dz != 0 && (!grid.IsPassable(next.X, cell.Z, 0f) ||
                            !grid.IsPassable(cell.X, next.Z, 0f))) continue;
                    float candidate = cost + grid.CellSize * (dx != 0 && dz != 0 ? MathF.Sqrt(2f) : 1f);
                    if (distance.TryGetValue(next, out float previous) && candidate >= previous) continue;
                    distance[next] = candidate;
                    queue.Enqueue(next, candidate);
                }
        }
        return float.PositiveInfinity;
    }
}
