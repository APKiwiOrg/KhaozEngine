using System;
using System.Diagnostics;
using KhaozEngine.Navigation;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;

namespace KhaozEngine.Tests.Navigation;

public sealed class GridSearchSynchronousTimingTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Diagnostic")]
    public void ReportsWarmedRegionQueryTimes()
    {
        if (Environment.GetEnvironmentVariable("KE_GRID_SEARCH_TIMING") != "1") return;

        foreach (var query in TargetedQueries())
        {
            NavPath Run() => query.Planner.FindPath(query.Start, query.Goal, 0f, query.Budget);
            for (int warm = 0; warm < 8; warm++) GC.KeepAlive(Run());

            var samples = new double[31];
            for (int i = 0; i < samples.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                GC.KeepAlive(Run());
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }
            Array.Sort(samples);
            output.WriteLine($"start={query.Start} anchor={query.Goal.Anchor} extent={query.Goal.HorizontalExtent} " +
                $"cap={query.Budget.MaxExpandedNodes} walkOnly={query.Budget.WalkOnly} status={query.Status} " +
                $"median={samples[samples.Length / 2]:F4} ms p95={samples[29]:F4} ms");
        }
    }
}
