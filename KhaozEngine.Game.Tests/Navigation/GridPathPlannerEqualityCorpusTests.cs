using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

/// <summary>
/// Pins the reused-scratch planner (KhaozEngine#1288) to the v20.24.0 algorithm: every query of a seeded
/// corpus returns exactly the status and waypoints of <see cref="ReferenceGridPathPlanner"/>. One planner
/// serves every query of its space, so point and region queries interleave on the same scratch.
/// </summary>
public class GridPathPlannerEqualityCorpusTests
{
    const int Spaces = 40;
    const int QueriesPerSpace = 40;

    [Fact]
    public void PointQueriesOnHeightlessGridsMatchTheReference()
    {
        var seen = new StatusTally();
        for (int seed = 1; seed <= Spaces; seed++)
        {
            var corpus = new PlannerCorpus(seed);
            NavSpace space = corpus.NextSpace(surfaces: false);
            var planner = new GridPathPlanner(space);
            var reference = new ReferenceGridPathPlanner(space);
            for (int q = 0; q < QueriesPerSpace; q++)
            {
                Vector3 start = corpus.NextPoint(), goal = corpus.NextPoint();
                float radius = corpus.NextRadius();
                PathQueryBudget budget = corpus.NextBudget();
                AssertSame(reference.FindPath(start, goal, radius, budget),
                    planner.FindPath(start, goal, radius, budget), seen, $"seed {seed} query {q}");
            }
        }
        seen.AssertCoversEveryStatus();
    }

    [Fact]
    public void LayeredPointAndRegionQueriesMatchTheReference()
    {
        var seen = new StatusTally();
        for (int seed = 1; seed <= Spaces; seed++)
        {
            var corpus = new PlannerCorpus(1000 + seed);
            NavSpace space = corpus.NextSpace(surfaces: true);
            float hopCost = corpus.NextBool() ? 4f : 2f;
            var planner = new GridPathPlanner(space, hopCost);
            var reference = new ReferenceGridPathPlanner(space, hopCost);
            for (int q = 0; q < QueriesPerSpace; q++)
                RunQuery(corpus, planner, reference, corpus.NextRadius(), seen, $"seed {1000 + seed} query {q}");
        }
        seen.AssertCoversEveryStatus();
    }

    [Fact]
    public void GuardedPointAndRegionQueriesMatchTheReference()
    {
        var seen = new StatusTally();
        for (int seed = 1; seed <= Spaces; seed++)
        {
            var corpus = new PlannerCorpus(2000 + seed);
            NavSpace space = corpus.NextSpace(surfaces: true);
            NavTraversalGraph graph = corpus.NextGraph();
            var planner = new GridPathPlanner(space, graph);
            var reference = new ReferenceGridPathPlanner(space, graph);
            for (int q = 0; q < QueriesPerSpace; q++)
                RunQuery(corpus, planner, reference, 0f, seen, $"seed {2000 + seed} query {q}");
        }
        seen.AssertCoversEveryStatus();
    }

    static void RunQuery(PlannerCorpus corpus, GridPathPlanner planner, ReferenceGridPathPlanner reference,
        float radius, StatusTally seen, string context)
    {
        Vector3 start = corpus.NextPoint();
        PathQueryBudget budget = corpus.NextBudget();
        if (corpus.NextBool())
        {
            Vector3 goal = corpus.NextPoint();
            AssertSame(reference.FindPath(start, goal, radius, budget),
                planner.FindPath(start, goal, radius, budget), seen, context + " point");
        }
        else
        {
            NavGoalRegion region = corpus.NextRegion();
            AssertSame(reference.FindPath(start, region, radius, budget),
                planner.FindPath(start, region, radius, budget), seen, context + " region");
        }
    }

    internal static void AssertSame(NavPath expected, NavPath actual, StatusTally? seen, string context)
    {
        Assert.True(expected.Status == actual.Status, $"{context}: status {actual.Status}, expected {expected.Status}");
        NavWaypoint[] want = expected.Waypoints.ToArray();
        NavWaypoint[] got = actual.Waypoints.ToArray();
        Assert.True(want.SequenceEqual(got),
            $"{context}: waypoints [{string.Join(", ", got)}], expected [{string.Join(", ", want)}]");
        seen?.Add(expected.Status);
    }

    internal sealed class StatusTally
    {
        readonly int[] _counts = new int[3];

        public void Add(NavPathStatus status) => _counts[(int)status]++;

        public void AssertCoversEveryStatus()
        {
            foreach (NavPathStatus status in Enum.GetValues<NavPathStatus>())
                Assert.True(_counts[(int)status] > 0, $"the corpus never produced a {status} route");
        }
    }
}
