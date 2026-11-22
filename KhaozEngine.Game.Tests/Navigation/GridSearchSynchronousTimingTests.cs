using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using KhaozEngine.Navigation;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Navigation.DisconnectedPlannerFixture;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;

namespace KhaozEngine.Tests.Navigation;

public sealed class GridSearchSynchronousTimingTests(ITestOutputHelper output)
{
    [Fact]
    public void RowsTakeTheExpectedPlannerDoor()
    {
        foreach (TimingRow row in Rows())
        {
            var before = row.Planner.WorkForTests;
            NavPath result = row.Run();
            var after = row.Planner.WorkForTests;
            Assert.True(result.Status == row.Status, $"{row.Name} returned {result.Status}, expected {row.Status}");
            Assert.Equal((row.Searches, row.Certificates),
                (after.Searches - before.Searches, after.Certificates - before.Certificates));
            if (row.Waypoints is int count) Assert.Equal(count, result.Waypoints.Count);
        }
    }

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void ReportsWarmedSynchronousQueryTimes()
    {
        if (Environment.GetEnvironmentVariable("KE_GRID_SEARCH_TIMING") != "1") return;

        output.WriteLine($"stopwatch_frequency={Stopwatch.Frequency}");
        Report("harness-control", () => NavPath.Unreachable, large: false);
        foreach (TimingRow row in Rows())
            Report(row.Name, row.Run, row.Large);
    }

    static IEnumerable<TimingRow> Rows()
    {
        foreach (RegionQuery query in TargetedQueries())
            yield return RegionRow(query, query.Name is "cap-4096" or "certificate-comparison-cap");

        var clear = Corridor();
        var end = End().Anchor;
        yield return new("point-legacy-line-of-sight", clear, () => clear.FindPath(Start, end, 0f, PathQueryBudget.Default),
            NavPathStatus.Complete, 0, 0, 1);

        var detour = new GridPathPlanner(NavSpace.Single(Grid(16, 3, (x, z) => x != 7 || z != 1)));
        var from = Start with { Z = 1.5f };
        var to = end with { Z = 1.5f };
        yield return new("point-legacy-astar-reconstruction", detour,
            () => detour.FindPath(from, to, 0f, PathQueryBudget.Default), NavPathStatus.Complete, 1, 0, 2);

        var space = NavSpace.Single(Grid(4, 1, (_, _) => true));
        var graph = new NavTraversalGraph(space, 0f, 1f,
            new[] { new NavTraversalLayer(4, 1, new[] { true, true, true, true }, new byte[] { 1, 1, 1, 0 }) },
            Array.Empty<NavLink>());
        var guarded = new GridPathPlanner(space, graph);
        var guardedEnd = End(4).Anchor;
        yield return new("point-guarded", guarded, () => guarded.FindPath(Start, guardedEnd, 0f, PathQueryBudget.Default),
            NavPathStatus.Complete, 1, 0, 3);

        foreach (RegionQuery query in AdditionalTimingQueries())
            yield return RegionRow(query, large: true);
    }

    static TimingRow RegionRow(RegionQuery query, bool large)
        => new("region-" + query.Name, query.Planner,
            () => query.Planner.FindPath(query.Start, query.Goal, 0f, query.Budget),
            query.Status, query.Searches, query.Certificates, query.Waypoints, large);

    void Report(string name, Func<NavPath> run, bool large)
    {
        int batch = large ? 1 : 128;
        int warmCalls = large ? 16 : 512;
        NavPath warmResult = run();
        for (int warm = 1; warm < warmCalls; warm++) warmResult = run();

        var samples = new long[large ? 63 : 151];
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        for (int i = 0; i < samples.Length; i++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int call = 0; call < batch; call++) GC.KeepAlive(run());
            samples[i] = Stopwatch.GetTimestamp() - start;
        }
        gen0 = GC.CollectionCount(0) - gen0;
        gen1 = GC.CollectionCount(1) - gen1;
        gen2 = GC.CollectionCount(2) - gen2;
        string rawTicks = string.Join(",", samples);
        Array.Sort(samples);
        double nanosecondsPerTickPerQuery = 1e9 / Stopwatch.Frequency / batch;
        double median = samples[samples.Length / 2] * nanosecondsPerTickPerQuery;
        double p95 = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1] * nanosecondsPerTickPerQuery;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"name={name} status={warmResult.Status} waypoints={warmResult.Waypoints.Count} batch={batch} " +
            $"median_ns={median:F3} p95_ns={p95:F3} gc={gen0},{gen1},{gen2}"));
        output.WriteLine($"name={name} raw_batch_ticks={rawTicks}");
    }

    /// <summary>A warmed timing input and the route door the ordinary counter test verifies.</summary>
    readonly record struct TimingRow(string Name, GridPathPlanner Planner, Func<NavPath> Run, NavPathStatus Status,
        int Searches, int Certificates, int? Waypoints, bool Large = false);
}
