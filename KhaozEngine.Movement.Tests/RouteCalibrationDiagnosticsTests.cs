using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using System.Runtime.CompilerServices;
using KhaozEngine.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

[Collection("AllocSensitive")]
public sealed class RouteCalibrationDiagnosticsTests(ITestOutputHelper output)
{
    const long SmallHeapBudget = 16 * 1024 * 1024;
    const long LargeHeapBudget = 16 * 1024 * 1024;
    const int WarmCalls = 8;
    const int PreludeBatches = 8;
    const int Samples = 31;

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void ReportsRouteCalibrationTimes()
    {
        if (Environment.GetEnvironmentVariable("KE_ROUTE_CALIBRATION_TIMING") != "1") return;
        output.WriteLine($"stopwatch_frequency={Stopwatch.Frequency}");
        ReportRows<TimedClock>();
    }

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void ChecksRouteCalibrationWindows()
    {
        if (Environment.GetEnvironmentVariable("KE_ROUTE_CALIBRATION_WINDOWS") != "1") return;
        ReportRows<UntimedClock>();
    }

    void ReportRows<TClock>() where TClock : struct, ISampleClock
    {
        List<string> invalidWindows = [];
        foreach (var fixture in RouteCalibrationFixture.SearchRows())
            ReportFixture(fixture, sliced: false);
        foreach (var fixture in RouteCalibrationFixture.StraighteningRows())
            ReportFixture(fixture, sliced: false);
        foreach (var fixture in RouteCalibrationFixture.WholeRoutes())
            ReportFixture(fixture, sliced: true);

        if (!TClock.IsTimed)
            Assert.True(invalidWindows.Count == 0,
                $"Expected a held no-GC region and zero collections for every row. Invalid windows: {string.Join(", ", invalidWindows)}");

        void ReportFixture(RouteCalibrationFixture fixture, bool sliced)
        {
            using var pool = fixture.CreatePool();
            string units = DescribeWork(fixture, pool);
            if (!sliced)
            {
                ReportAt(int.MaxValue);
                return;
            }

            foreach (int quota in new[] { 64, 256, 1024, 4096, 16384 }) ReportAt(quota);

            void ReportAt(int quota)
            {
                if (!Report<TClock>(fixture, pool, sliced, new(quota), units) && !TClock.IsTimed)
                    invalidWindows.Add($"{fixture.Name}/{quota}");
            }
        }
    }

    static string DescribeWork(RouteCalibrationFixture fixture, PathPlanWorkspaces pool)
    {
        using var request = fixture.Begin(pool);
        var work = new PathWorkByKind();
        request.AdvanceForTests(new(int.MaxValue), work);
        Assert.Equal(PathPlanState.Ready, request.State);
        Assert.Equal(request.WorkSpent, work.Total);
        return CalibrationWork.Describe(work);
    }

    bool Report<TClock>(RouteCalibrationFixture fixture, PathPlanWorkspaces pool, bool sliced, PathWorkBudget quota,
        string units) where TClock : struct, ISampleClock
    {
        // Reuse the large-row window from the synchronous movement diagnostic, with one request per batch.
        var shape = ReadWarmedShape(fixture, pool, quota);
        int sampleSize = sliced ? shape.Advances : 1;
        var samples = new long[Samples * sampleSize];
        for (int warm = 0; warm < WarmCalls; warm++)
            RunBatch<UntimedClock>(fixture, pool, sliced, quota, samples.AsSpan(0, sampleSize));

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
            for (int prelude = 0; prelude < PreludeBatches; prelude++)
                RunBatch<UntimedClock>(fixture, pool, sliced, quota, samples.AsSpan(0, sampleSize));
            for (int i = 0; i < Samples; i++)
                RunBatch<TClock>(fixture, pool, sliced, quota, samples.AsSpan(i * sampleSize, sampleSize));
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

        string mode = sliced ? "advance" : "request";
        string? rawTicks = TClock.IsTimed ? string.Join(",", samples) : null;
        string timing = TClock.IsTimed ? TimingFields(samples) : "";
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"name={fixture.Name} mode={mode} quota={quota.Units} status={shape.Status} waypoints={shape.Waypoints} " +
            $"advances={shape.Advances} max_advance_units={shape.LargestAdvanceUnits} " +
            $"begin_bytes={shape.BeginBytes} request_bytes={shape.RequestBytes} {timing}" +
            $"gc={gen0},{gen1},{gen2} nogc={(held ? "held" : "lost")} " +
            $"window_bytes={windowBytes} thread_bytes={threadBytes} units={units}"));
        if (rawTicks is not null)
            output.WriteLine($"name={fixture.Name} mode={mode} quota={quota.Units} raw_ticks={rawTicks}");
        return held && gen0 == 0 && gen1 == 0 && gen2 == 0;
    }

    static void RunBatch<TClock>(RouteCalibrationFixture fixture, PathPlanWorkspaces pool, bool sliced,
        PathWorkBudget quota, Span<long> samples) where TClock : struct, ISampleClock
    {
        if (!sliced)
        {
            long start = TClock.GetTimestamp();
            using var request = fixture.Begin(pool);
            request.Advance(quota);
            samples[0] = TClock.GetTimestamp() - start;
            if (request.State != PathPlanState.Ready) throw new InvalidOperationException("The unbounded request did not finish.");
            return;
        }

        using var slicedRequest = fixture.Begin(pool);
        for (int i = 0; i < samples.Length; i++)
        {
            long start = TClock.GetTimestamp();
            slicedRequest.Advance(quota);
            samples[i] = TClock.GetTimestamp() - start;
            bool published = slicedRequest.State == PathPlanState.Ready;
            if (published != (i == samples.Length - 1))
                throw new InvalidOperationException("The request published on a different advance from its warm call.");
        }
    }

    static RequestShape ReadWarmedShape(RouteCalibrationFixture fixture, PathPlanWorkspaces pool, PathWorkBudget quota)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        using var request = fixture.Begin(pool);
        long begun = GC.GetAllocatedBytesForCurrentThread();
        int advances = 0;
        long largest = 0;
        while (request.State == PathPlanState.Pending)
        {
            long prior = request.WorkSpent;
            request.Advance(quota);
            largest = Math.Max(largest, request.WorkSpent - prior);
            advances++;
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        NavPath path = request.Result ?? throw new InvalidOperationException("The request did not publish.");
        return new(advances, largest, begun - start, bytes, path.Status, path.Waypoints.Count);
    }

    static string TimingFields(long[] samples)
    {
        Array.Sort(samples);
        double ns = 1e9 / Stopwatch.Frequency;
        double median = samples[samples.Length / 2] * ns;
        double p95 = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1] * ns;
        return string.Create(CultureInfo.InvariantCulture,
            $"median_ns={median:F3} p95_ns={p95:F3} max_ns={samples[^1] * ns:F3} ");
    }

    static void CompactHeaps()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

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

    readonly record struct RequestShape(int Advances, long LargestAdvanceUnits, long BeginBytes, long RequestBytes,
        NavPathStatus Status, int Waypoints);
}
