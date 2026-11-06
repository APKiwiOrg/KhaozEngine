using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class GridPathPlannerSpatialBoundTests
{
    [Fact]
    public void PointPartialProgressUsesPhysicalFeetBelowARoof()
    {
        // #1712: a cheap hop reaches the goal's XZ on a roof, but ordinary ground is
        // closer to its physical feet. The budget pops both before choosing a Partial.
        NavGrid ground = NavGrid.FromBlockedSurfaces(5, 1, 1f, 0f, 0f,
            new bool[5], new float[5]);
        NavGrid roof = NavGrid.FromBlockedSurfaces(5, 1, 1f, 0f, 0f,
            new[] { true, true, true, true, false }, new[] { 10f, 10f, 10f, 10f, 10f });
        var space = new NavSpace(new[] { ground, roof }, new[] { new NavLink(0, 0, 0, 1, 4, 0) { Kind = NavLinkKind.Hop } });
        var budget = new PathQueryBudget { MaxExpandedNodes = 3, SnapRadius = 0f };

        NavPath path = new GridPathPlanner(space, hopCostCells: 1f).FindPath(new(0.5f, 0f, 0.5f),
            new Vector3(4.5f, 0f, 0.5f), 0f, budget);

        Assert.Equal(NavPathStatus.Partial, path.Status);
        Assert.NotEmpty(path.Waypoints);
        Assert.Equal(0, path.Waypoints[^1].Layer);
        Assert.Equal(new Vector2(1.5f, 0.5f), path.Waypoints[^1].Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DiagonalCrossLayerStairsKeepAUsefulBoundWithDrops(bool region)
    {
        // #1712: each diagonal stair covers its span, keeping the spatial bound at one.
        // Alternating layers retains the cross-layer search. Dijkstra floods both open planes.
        const int side = 128, target = 101;
        var heights = new float[side * side];
        heights[120 * side + 120] = 3f;
        NavGrid lower = NavGrid.FromBlockedSurfaces(side, side, 1f, 0f, 0f, new bool[heights.Length], heights);
        var upperHeights = new float[heights.Length];
        System.Array.Fill(upperHeights, 4f);
        NavGrid upper = NavGrid.FromBlockedSurfaces(side, side, 1f, 0f, 0f, new bool[heights.Length], upperHeights);
        var links = new List<NavLink>();
        for (int i = 0; i < target; i++)
        {
            links.Add(new NavLink(0, i, i, 1, i + 1, i + 1));
            links.Add(new NavLink(1, i, i, 0, i + 1, i + 1));
        }
        links.Add(new NavLink(0, 120, 120, 0, 121, 120) { Kind = NavLinkKind.Drop });
        var planner = new GridPathPlanner(new NavSpace(new[] { lower, upper }, links));
        Vector3 start = new(0.5f, 0f, 0.5f), goal = new(target + 0.5f, 4f, target + 0.5f);
        var budget = new PathQueryBudget { MaxExpandedNodes = 1024, SnapRadius = 0f };

        NavPath path = region
            ? planner.FindPath(start, new NavGoalRegion(goal, 0f, feet => feet == goal), 0f, budget)
            : planner.FindPath(start, goal, 0f, budget);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.NotEmpty(path.Waypoints);
        Assert.Equal(new Vector2(goal.X, goal.Z), path.Waypoints[^1].Position);
        Assert.Equal(1, path.Waypoints[^1].Layer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SeededMultilayerCostsEqualExhaustiveDijkstra(bool differentFrames)
    {
        // #1712: a popped node must remain final through metric-priced stairs and same-layer Drops.
        const int seeds = 12, queries = 8;
        for (int seed = 1; seed <= seeds; seed++)
        {
            var graph = new SpatialBoundGraph(seed, differentFrames);
            var planner = new GridPathPlanner(graph.Space, graph.Traversal);
            var old = new ReferenceGridPathPlanner(graph.Space, graph.Traversal);
            var random = new Random(seed + 100);
            for (int q = 0; q < queries; q++)
            {
                var start = new SpatialBoundGraph.Node(random.Next(3), random.Next(8), random.Next(8));
                var goal = new SpatialBoundGraph.Node(random.Next(3), random.Next(8), random.Next(8));
                Vector3 from = graph.Feet(start), to = graph.Feet(goal);
                var region = new NavGoalRegion(to, 0f, feet => feet == to);
                NavPath previous = old.FindPath(from, region, 0f, PathQueryBudget.Default);
                float expected = graph.ShortestCost(start, goal);
                NavPath point = planner.FindPath(from, to, 0f, PathQueryBudget.Default);
                NavPath area = planner.FindPath(from, region, 0f, PathQueryBudget.Default);
                Assert.Equal(NavPathStatus.Complete, previous.Status);
                foreach (NavPath path in new[] { point, area })
                {
                    Assert.Equal(previous.Status, path.Status);
                    Assert.True(MathF.Abs(expected - graph.PathCost(start, path)) <= 0.00001f,
                        $"seed {seed}, query {q}, frames {differentFrames}: cost {graph.PathCost(start, path)}, Dijkstra {expected}");
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleLayerPreservingLinksKeepTheReferencePaths(bool links)
    {
        var corpus = new PlannerCorpus(1712);
        NavSpace original = corpus.NextSpace(surfaces: true, maxLayers: 1);
        NavSpace space = links ? new NavSpace(original.Layers, new[]
        {
            new NavLink(0, 1, 1, 0, 2, 1),
            new NavLink(0, 1, 1, 0, 3, 3) { Kind = NavLinkKind.Hop },
        }) : new NavSpace(original.Layers);
        var planner = new GridPathPlanner(space);
        var reference = new ReferenceGridPathPlanner(space);
        for (int q = 0; q < 40; q++)
        {
            Vector3 start = corpus.NextPoint(), goal = corpus.NextPoint();
            PathQueryBudget budget = corpus.NextBudget();
            GridPathPlannerEqualityCorpusTests.AssertSame(reference.FindPath(start, goal, 0f, budget),
                planner.FindPath(start, goal, 0f, budget), null, $"query {q}");
            NavGoalRegion region = corpus.NextRegion();
            GridPathPlannerEqualityCorpusTests.AssertSame(reference.FindPath(start, region, 0f, budget),
                planner.FindPath(start, region, 0f, budget), null, $"region query {q}");
        }
    }


    [Fact]
    public void CheapLinkDetoursDoNotLoseToThePointLineOfSightShortcut()
    {
        // #1712: seeing the goal does not prove walking is cheapest when links undercut distance.
        NavGrid grid = NavGrid.FromWalkable(10, 1, 1f, 0f, 0f, (_, _) => true);
        var space = new NavSpace(new[] { grid }, new[]
        {
            new NavLink(0, 0, 0, 0, 9, 0) { Kind = NavLinkKind.Hop },
            new NavLink(0, 9, 0, 0, 3, 0) { Kind = NavLinkKind.Hop },
        });

        NavPath path = new GridPathPlanner(space, hopCostCells: 1f).FindPath(new(0.5f, 0f, 0.5f), new Vector3(3.5f, 0f, 0.5f),
            0f, PathQueryBudget.Default);

        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Contains(path.Waypoints, waypoint => waypoint.Position == new Vector2(9.5f, 0.5f));
    }

}
