using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime;
using System.Runtime.CompilerServices;
using KhaozEngine.Navigation;
using Xunit;
using Xunit.Abstractions;
using static KhaozEngine.Tests.Navigation.DisconnectedPlannerFixture;
using static KhaozEngine.Tests.Navigation.GridSearchRequestFixtures;

namespace KhaozEngine.Tests.Navigation;

[Collection("AllocSensitive")]
public sealed class GridSearchSynchronousTimingTests(ITestOutputHelper output)
{
    const long SmallHeapBudget = 16 * 1024 * 1024;
    const long LargeHeapBudget = 16 * 1024 * 1024;

    // Unmeasured batches run inside the region after the heap is prepared and before the first recorded sample.
    const int PreludeBatches = 8;

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
    public void AdditionalRowsKeepTheirIntendedSearchAndCertificateSizes()
    {
        RegionQuery[] rows = AdditionalTimingQueries().ToArray();
        RegionQuery bounded = Assert.Single(rows, row => row.Name == "bounded-astar-no-reconstruction");
        // On the same topology, cell 1 is the 1,024th node popped from cell 1,024.
        // The measured goal is across the blocked cell and preserves the no-reconstruction result.
        var witness = Member(new Vector3(1.5f, 0f, 0.5f));
        NavPath reached = bounded.Planner.FindPath(bounded.Start, witness, 0f, bounded.Budget);
        Assert.Equal(NavPathStatus.Complete, reached.Status);
        Assert.Equal(1023, reached.Waypoints.Count);
        Assert.Equal(1024, bounded.Budget.MaxExpandedNodes);

        RegionQuery nearCap = Assert.Single(rows, row => row.Name == "certificate-near-comparison-cap");
        NavSpace space = Assert.IsType<NavSpace>(nearCap.ReferenceSpace);
        NavGrid grid = Assert.Single(space.Layers);
        (int startX, int startZ) = grid.CellOf(nearCap.Start.X, nearCap.Start.Z);
        var connectivity = PlannerConnectivity.For(space, null);
        Assert.Equal(1024, connectivity.Members(connectivity.ComponentOf(startZ * grid.Width + startX)).Length);

        int members = 0;
        for (int z = 0; z < grid.Height; z++)
            for (int x = 0; x < grid.Width; x++)
            {
                if (!grid.IsPassable(x, z, 0f)) continue;
                Vector2 center = grid.CellCenter(x, z);
                var feet = new Vector3(center.X, grid.RestingHeightAt(x, z)!.Value, center.Y);
                NavGoalRegion goal = nearCap.Goal;
                if (Math.Abs((double)feet.X - goal.Anchor.X) <= goal.HorizontalExtent &&
                    Math.Abs((double)feet.Z - goal.Anchor.Z) <= goal.HorizontalExtent && goal.Contains(feet))
                    members++;
            }
        Assert.Equal(256, members);
    }

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void ReportsWarmedSynchronousQueryTimes()
    {
        if (Environment.GetEnvironmentVariable("KE_GRID_SEARCH_TIMING") != "1") return;

        output.WriteLine($"stopwatch_frequency={Stopwatch.Frequency}");
        ReportRows<TimedClock>();
    }

    [Fact]
    [Trait("Category", "Diagnostic")]
    internal void ChecksWarmedSynchronousSamplingWindows()
    {
        if (Environment.GetEnvironmentVariable("KE_GRID_SEARCH_WINDOWS") != "1") return;

        ReportRows<UntimedClock>();
    }

    void ReportRows<TClock>() where TClock : struct, ISampleClock
    {
        var invalidWindows = new List<string>();
        ReportWindow("harness-control", static () => NavPath.Unreachable, large: false);
        foreach (TimingRow row in Rows())
            ReportWindow(row.Name, row.Run, row.Large);

        if (!TClock.IsTimed)
            Assert.True(invalidWindows.Count == 0,
                $"Expected a held no-GC region and zero collections for every row. Invalid windows: {string.Join(", ", invalidWindows)}");

        void ReportWindow(string name, Func<NavPath> run, bool large)
        {
            if (!Report<TClock>(name, run, large) && !TClock.IsTimed)
                invalidWindows.Add(name);
        }
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

    bool Report<TClock>(string name, Func<NavPath> run, bool large) where TClock : struct, ISampleClock
    {
        int batch = large ? 1 : 128;
        int warmCalls = large ? 16 : 512;
        NavPath warmResult = run();
        for (int warm = 1; warm < warmCalls; warm++) warmResult = run();

        var samples = new long[large ? 63 : 151];
        CompactHeaps();
        GC.WaitForPendingFinalizers();
        CompactHeaps();
        bool started = GC.TryStartNoGCRegion(SmallHeapBudget + LargeHeapBudget, LargeHeapBudget,
            disallowFullBlockingGC: true);
        bool held = false;
        int gen0, gen1, gen2;
        long windowBytes, threadBytes;
        try
        {
            gen0 = GC.CollectionCount(0);
            gen1 = GC.CollectionCount(1);
            gen2 = GC.CollectionCount(2);
            windowBytes = GC.GetTotalAllocatedBytes(precise: true);
            threadBytes = GC.GetAllocatedBytesForCurrentThread();
            // The collections, the reservation and the precise counter read leave caches cold and the reserved
            // allocation space untouched. These batches absorb that, so the recorded samples start warm.
            for (int prelude = 0; prelude < PreludeBatches; prelude++)
                for (int call = 0; call < batch; call++) GC.KeepAlive(run());
            for (int i = 0; i < samples.Length; i++)
            {
                long start = TClock.GetTimestamp();
                for (int call = 0; call < batch; call++) GC.KeepAlive(run());
                samples[i] = TClock.GetTimestamp() - start;
            }
            held = started && GCSettings.LatencyMode == GCLatencyMode.NoGCRegion;
            gen0 = GC.CollectionCount(0) - gen0;
            gen1 = GC.CollectionCount(1) - gen1;
            gen2 = GC.CollectionCount(2) - gen2;
            windowBytes = GC.GetTotalAllocatedBytes(precise: true) - windowBytes;
            threadBytes = GC.GetAllocatedBytesForCurrentThread() - threadBytes;
        }
        finally
        {
            if (started)
            {
                try { GC.EndNoGCRegion(); }
                catch (InvalidOperationException) { held = false; }
            }
        }

        string? rawTicks = null;
        string timingFields = "";
        if (TClock.IsTimed)
        {
            rawTicks = string.Join(",", samples);
            Array.Sort(samples);
            double nanosecondsPerTickPerQuery = 1e9 / Stopwatch.Frequency / batch;
            double median = samples[samples.Length / 2] * nanosecondsPerTickPerQuery;
            double p95 = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1] * nanosecondsPerTickPerQuery;
            timingFields = string.Create(CultureInfo.InvariantCulture, $"median_ns={median:F3} p95_ns={p95:F3} ");
        }
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"name={name} status={warmResult.Status} waypoints={warmResult.Waypoints.Count} batch={batch} " +
            $"{timingFields}gc={gen0},{gen1},{gen2} nogc={(held ? "held" : "lost")} " +
            $"window_bytes={windowBytes} thread_bytes={threadBytes}"));
        if (rawTicks is not null)
            output.WriteLine($"name={name} raw_batch_ticks={rawTicks}");

        return held && gen0 == 0 && gen1 == 0 && gen2 == 0;
    }

    static void CompactHeaps()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    // Value-type clocks share the sample loop without a mode branch between the timestamp reads.
    interface ISampleClock
    {
        static abstract bool IsTimed { get; }
        static abstract long GetTimestamp();
    }

    readonly struct TimedClock : ISampleClock
    {
        static bool ISampleClock.IsTimed => true;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long ISampleClock.GetTimestamp() => Stopwatch.GetTimestamp();
    }

    readonly struct UntimedClock : ISampleClock
    {
        static bool ISampleClock.IsTimed => false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long ISampleClock.GetTimestamp() => 0;
    }

    /// <summary>A warmed timing input and the route door the ordinary counter test verifies.</summary>
    readonly record struct TimingRow(string Name, GridPathPlanner Planner, Func<NavPath> Run, NavPathStatus Status,
        int Searches, int Certificates, int? Waypoints, bool Large = false);
}
