using System;
using System.Diagnostics;
using KhaozEngine.Navigation;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Navigation.DisconnectedPlannerFixture;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;

namespace KhaozEngine.Tests.Navigation;

public sealed class GridSearchSynchronousTimingTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Diagnostic")]
    public void ReportsWarmedSynchronousQueryTimes()
    {
        if (Environment.GetEnvironmentVariable("KE_GRID_SEARCH_TIMING") != "1") return;

        foreach (RegionQuery query in TargetedQueries())
            Report("region-" + query.Name, () => query.Planner.FindPath(query.Start, query.Goal, 0f, query.Budget));

        var clear = Corridor();
        var end = End().Anchor;
        Report("point-legacy-line-of-sight", () => clear.FindPath(Start, end, 0f, PathQueryBudget.Default));

        var detour = new GridPathPlanner(NavSpace.Single(Grid(16, 3, (x, z) => x != 7 || z != 1)));
        var from = Start with { Z = 1.5f };
        var to = end with { Z = 1.5f };
        Report("point-legacy-astar-reconstruction", () => detour.FindPath(from, to, 0f, PathQueryBudget.Default));

        var space = NavSpace.Single(Grid(4, 1, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(4, 1, new[] { true, true, true, true }, new byte[] { 1, 1, 1, 0 }) },
            Array.Empty<NavLink>());
        var guarded = new GridPathPlanner(space, graph);
        var guardedEnd = End(4).Anchor;
        Report("point-guarded", () => guarded.FindPath(Start, guardedEnd, 0f, PathQueryBudget.Default));
    }

    void Report(string name, Func<NavPath> run)
    {
        NavPath warmResult = run();
        for (int warm = 1; warm < 8; warm++) warmResult = run();

        var samples = new double[31];
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            GC.KeepAlive(run());
            samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        }
        Array.Sort(samples);
        output.WriteLine($"name={name} status={warmResult.Status} waypoints={warmResult.Waypoints.Count} " +
            $"median={samples[samples.Length / 2]:F4} ms p95={samples[29]:F4} ms");
    }
}
