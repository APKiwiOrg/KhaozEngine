using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using KhaozEngine.Navigation;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

[Collection("AllocSensitive")]
public sealed partial class RouteCalibrationDiagnosticsTests(ITestOutputHelper output)
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

        ReportPhases<TClock>(invalidWindows);

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
        var shape = ReadWarmedShape(fixture, pool, quota);
        long[] charges = ReplayCharges(fixture, pool, quota, shape);
        int sampleSize = sliced ? charges.Length : 1;
        var window = MeasureWindow(sampleSize,
            samples => RunBatch<UntimedClock>(fixture, pool, sliced, quota, shape.Path, charges, samples),
            samples => RunBatch<TClock>(fixture, pool, sliced, quota, shape.Path, charges, samples));

        string mode = sliced ? "advance" : "request";
        string key = $"name={fixture.Name} mode={mode} quota={quota.Units}";
        string? rawTicks = TClock.IsTimed ? string.Join(",", window.Samples) : null;
        string slowest = TClock.IsTimed && sliced ? SlowestAdvance(window.Samples, charges) : "";
        string timing = TClock.IsTimed ? TimingFields(window.Samples) : "";
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{key} status={shape.Path.Status} waypoints={shape.Path.Waypoints.Count} " +
            $"advances={charges.Length} max_advance_units={charges.Max()} " +
            $"begin_bytes={shape.BeginBytes} request_bytes={shape.RequestBytes} {timing}{slowest}" +
            $"{window.ValidityFields()} units={units}"));
        if (rawTicks is not null)
        {
            if (sliced)
                output.WriteLine($"{key} advance_units={string.Join(",", charges)}");
            output.WriteLine($"{key} raw_ticks={rawTicks}");
        }
        return window.IsValid;
    }

    delegate void SampleBatch(Span<long> samples);

    static SampleWindow MeasureWindow(int sampleSize, SampleBatch runUntimed, SampleBatch run)
    {
        // Reuse the large-row window from the synchronous movement diagnostic.
        var samples = new long[Samples * sampleSize];
        for (int warm = 0; warm < WarmCalls; warm++)
            runUntimed(samples.AsSpan(0, sampleSize));

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
                runUntimed(samples.AsSpan(0, sampleSize));
            for (int i = 0; i < Samples; i++)
                run(samples.AsSpan(i * sampleSize, sampleSize));
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

        return new(samples, gen0, gen1, gen2, held, windowBytes, threadBytes);
    }

    static void RunBatch<TClock>(RouteCalibrationFixture fixture, PathPlanWorkspaces pool, bool sliced,
        PathWorkBudget quota, NavPath expected, long[] charges, Span<long> samples) where TClock : struct, ISampleClock
    {
        if (!sliced)
        {
            long start = TClock.GetTimestamp();
            using var request = fixture.Begin(pool);
            request.Advance(quota);
            long end = TClock.GetTimestamp();
            samples[0] = end - start;

            if (charges.Length != 1 || request.WorkSpent != charges[0])
                throw new InvalidOperationException("The unbounded request spent different work from its warm replay.");
            ValidateResult(expected, request.Result);
            return;
        }

        using var slicedRequest = fixture.Begin(pool);
        long prior = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            long start = TClock.GetTimestamp();
            slicedRequest.Advance(quota);
            long end = TClock.GetTimestamp();
            samples[i] = end - start;

            long spent = slicedRequest.WorkSpent;
            if (spent - prior != charges[i])
                throw new InvalidOperationException($"Advance {i + 1} spent different work from its warm replay.");
            prior = spent;
            ValidatePublication(slicedRequest, i == samples.Length - 1);
        }
        ValidateResult(expected, slicedRequest.Result);
    }

    static RequestShape ReadWarmedShape(RouteCalibrationFixture fixture, PathPlanWorkspaces pool, PathWorkBudget quota)
    {
        long start = GC.GetAllocatedBytesForCurrentThread();
        using var request = fixture.Begin(pool);
        long begun = GC.GetAllocatedBytesForCurrentThread();
        int advances = 0;
        while (request.State == PathPlanState.Pending)
        {
            request.Advance(quota);
            advances++;
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        NavPath path = request.Result ?? throw new InvalidOperationException("The request did not publish.");
        return new(advances, begun - start, bytes, path);
    }

    static long[] ReplayCharges(RouteCalibrationFixture fixture, PathPlanWorkspaces pool, PathWorkBudget quota,
        RequestShape shape)
    {
        var charges = new long[shape.Advances];
        using var request = fixture.Begin(pool);
        long prior = 0;
        for (int i = 0; i < charges.Length; i++)
        {
            request.Advance(quota);
            long spent = request.WorkSpent;
            charges[i] = spent - prior;
            prior = spent;
            ValidatePublication(request, i == charges.Length - 1);
        }
        ValidateResult(shape.Path, request.Result);
        return charges;
    }

    static void ValidatePublication(PathPlanRequest request, bool last)
    {
        if (request.State != (last ? PathPlanState.Ready : PathPlanState.Pending))
            throw new InvalidOperationException("The request published on a different advance from its warm call.");
    }

    static void ValidateResult(NavPath expected, NavPath? actual)
    {
        if (actual is null || expected.Status != actual.Status || expected.Waypoints.Count != actual.Waypoints.Count)
            throw new InvalidOperationException("The request published a different route from its warm call.");
        for (int i = 0; i < expected.Waypoints.Count; i++)
        {
            NavWaypoint a = expected.Waypoints[i], b = actual.Waypoints[i];
            if (a.Layer != b.Layer || a.Kind != b.Kind ||
                BitConverter.SingleToInt32Bits(a.Position.X) != BitConverter.SingleToInt32Bits(b.Position.X) ||
                BitConverter.SingleToInt32Bits(a.Position.Y) != BitConverter.SingleToInt32Bits(b.Position.Y))
                throw new InvalidOperationException($"Waypoint {i} differs from its warm call.");
        }
    }

    static string SlowestAdvance(long[] samples, long[] charges)
    {
        int index = Array.IndexOf(samples, samples.Max());
        int ordinal = index % charges.Length;
        return string.Create(CultureInfo.InvariantCulture,
            $"slowest_advance_batch={index / charges.Length + 1} slowest_advance_ordinal={ordinal + 1} " +
            $"slowest_advance_units={charges[ordinal]} ");
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

    readonly record struct RequestShape(int Advances, long BeginBytes, long RequestBytes, NavPath Path);

    readonly record struct SampleWindow(long[] Samples, int Gen0, int Gen1, int Gen2, bool Held,
        long WindowBytes, long ThreadBytes)
    {
        internal bool IsValid => Held && Gen0 == 0 && Gen1 == 0 && Gen2 == 0;

        internal string ValidityFields() => string.Create(CultureInfo.InvariantCulture,
            $"gc={Gen0},{Gen1},{Gen2} nogc={(Held ? "held" : "lost")} " +
            $"window_bytes={WindowBytes} thread_bytes={ThreadBytes}");
    }
}
