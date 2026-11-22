using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime;
using System.Runtime.CompilerServices;
using KhaozEngine.Movement;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

[Collection("AllocSensitive")]
public sealed class RouteStraighteningSynchronousTimingTests(ITestOutputHelper output)
{
    const long SmallHeapBudget = 16 * 1024 * 1024;
    const long LargeHeapBudget = 16 * 1024 * 1024;
    const int PreludeBatches = 8;

    [Fact]
    public void RowsExerciseTheExpectedWorkload()
    {
        // Horizontal sweep rectangles: small 6 cells plus 2 endpoint cells, with side lines straddling
        // two rows each. Medium: 40 x 9 plus two 9 x 9 endpoints. Large: 176 x 49 plus two 49 x 49 endpoints.
        // The side refusal consumes the whole center line, two side endpoint boxes, and 9 side sweep cells.
        (string Name, int Result, long Cells)[] expected =
        [
            ("segment-small", 1, 8), ("straighten-small", 1, 40),
            ("segment-medium", 1, 522), ("straighten-medium", 1, 1566),
            ("segment-large", 1, 13426), ("straighten-large", 1, 40278),
            ("segment-side-refusal", 1, 8), ("straighten-side-refusal", 2, 21),
            ("segment-refused-endpoint", 0, 0), ("straighten-unreachable", 0, 0),
            ("straighten-single", 1, 0), ("straighten-malformed", 2, 0),
            ("straighten-special", 2, 0), ("segment-stair", 1, 4), ("straighten-layer", 2, 0),
        ];
        var rows = StraighteningTimingRow.Create().ToDictionary(row => row.Name);
        Assert.Equal(expected.Length, rows.Count);
        foreach (var (name, result, cells) in expected)
        {
            using var work = new NavFootprintDiagnostics();
            Assert.Equal(result, rows[name].Run());
            Assert.Equal(cells, work.CandidateCells);
        }
    }

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void ReportsWarmedSynchronousQueryTimes()
    {
        if (Environment.GetEnvironmentVariable("KE_ROUTE_STRAIGHTENING_TIMING") != "1") return;

        output.WriteLine($"stopwatch_frequency={Stopwatch.Frequency}");
        ReportRows<TimedClock>();
    }

    [Fact]
    [Trait("Category", "Diagnostic")]
    public void ChecksWarmedSynchronousSamplingWindows()
    {
        if (Environment.GetEnvironmentVariable("KE_ROUTE_STRAIGHTENING_WINDOWS") != "1") return;

        ReportRows<UntimedClock>();
    }

    void ReportRows<TClock>() where TClock : struct, ISampleClock
    {
        var invalidWindows = new List<string>();
        foreach (StraighteningTimingRow row in StraighteningTimingRow.Create())
            if (!Report<TClock>(row.Name, row.Run, row.Large) && !TClock.IsTimed)
                invalidWindows.Add(row.Name);

        if (!TClock.IsTimed)
            Assert.True(invalidWindows.Count == 0,
                $"Expected a held no-GC region and zero collections for every row. Invalid windows: {string.Join(", ", invalidWindows)}");
    }

    bool Report<TClock>(string name, Func<int> run, bool large) where TClock : struct, ISampleClock
    {
        int batch = large ? 1 : 32;
        int warmCalls = large ? 8 : 64;
        int warmResult = run();
        for (int warm = 1; warm < warmCalls; warm++) warmResult = run();

        var samples = new long[large ? 31 : 63];
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
                for (int call = 0; call < batch; call++) _ = run();
            for (int i = 0; i < samples.Length; i++)
            {
                long start = TClock.GetTimestamp();
                for (int call = 0; call < batch; call++) _ = run();
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
            $"name={name} result={warmResult} batch={batch} " +
            $"{timingFields}gc={gen0},{gen1},{gen2} nogc={(held ? "held" : "lost")} " +
            $"window_bytes={windowBytes} thread_bytes={threadBytes}" +
            (rawTicks is null ? "" : $" raw_batch_ticks={rawTicks}")));

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
}
