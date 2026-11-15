using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Collection("AllocSensitive")]
public class TileWaterTraceMemoLookupDiagnosticTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "Diagnostic")]
    public void ReportOneFullTableLookupWorkload()
    {
        using var original = WaterFloatingPointEnvironment.Capture();
        if (!TileWaterFloatingPointState.TryRead(out _))
        {
            output.WriteLine("Memo unavailable on this runtime. No lookup price measured.");
            return;
        }
        var field = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var table = new TileWaterTraceMemo.Table();
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var identity = MovementWaterFieldTests.Identity;
        bool priorBypass = TileMediumSampler.BypassTraceMemoForTests;
        bool priorEnabled = TileWaterQueryWork.Enabled;
        TileMediumSampler.BypassTraceMemoForTests = true;
        TileWaterQueryWork.Enabled = false;
        try
        {
            for (int i = 0; i < TileWaterTraceMemo.EntryLimit; i++)
            {
                var query = Query(i);
                var result = field.TraceWater(query, Vector3.Zero, identity, spans, contacts);
                Assert.Equal(MovementAvailability.Known, result.Availability);
                Assert.InRange(result.SpansWritten, 1, TileWaterTraceMemo.SpanLimit);
                Assert.InRange(result.ContactsWritten, 1, TileWaterTraceMemo.ContactLimit);
                Assert.True(TileWaterFloatingPointState.TryRead(out ulong controls));
                var key = new TileWaterTraceMemo.Key(query, Vector3.Zero, spans.Length, contacts.Length, controls);
                table.Store(key, result, spans, contacts);
            }
            Assert.Equal(TileWaterTraceMemo.EntryLimit, table.CountForTests);
            var hitQuery = Query(TileWaterTraceMemo.EntryLimit - 1);
            var missQuery = Query(TileWaterTraceMemo.EntryLimit);
            Assert.True(Lookup(hitQuery));
            Assert.False(Lookup(missQuery));
            const int samples = 10000;
            int hits = 0;
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < samples; i++) if (Lookup(hitQuery)) hits++;
            long hitTicks = Stopwatch.GetTimestamp() - start;
            Assert.Equal(samples, hits);
            int misses = 0;
            start = Stopwatch.GetTimestamp();
            for (int i = 0; i < samples; i++) if (!Lookup(missQuery)) misses++;
            long missTicks = Stopwatch.GetTimestamp() - start;
            Assert.Equal(samples, misses);
            var wet = Query(0);
            var dry = TileWaterTraceMemoObservationTests.Query(MovementWaterFieldTests.Space, dry: true);
            field.TraceWater(dry, Vector3.Zero, identity, spans, contacts);
            long wetTicks = TraceTicks(wet);
            long dryTicks = TraceTicks(dry);
            double nanoseconds = 1e9 / Stopwatch.Frequency;
            output.WriteLine($"utc={DateTime.UtcNow:O}, runtime={RuntimeInformation.FrameworkDescription}, arch={RuntimeInformation.ProcessArchitecture}, tiered={Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")}");
            output.WriteLine($"entries={table.CountForTests}, samples={samples}, hit-position=last, hit-ns={hitTicks * nanoseconds / samples:R}, full-miss-ns={missTicks * nanoseconds / samples:R}, wet-trace-ns={wetTicks * nanoseconds:R}, zero-contact-trace-ns={dryTicks * nanoseconds:R}");

            ReportDryShortcutAgainstExactResultLookup();

            bool Lookup(in MovementMediumSweepQuery query)
            {
                if (!TileWaterFloatingPointState.TryRead(out ulong controls)) return false;
                var key = new TileWaterTraceMemo.Key(query, Vector3.Zero, spans.Length, contacts.Length, controls);
                return table.TryRead(key, query.Body.CurrentSpace, identity, spans, contacts, out _);
            }

            long TraceTicks(in MovementMediumSweepQuery query)
            {
                long before = Stopwatch.GetTimestamp();
                var result = field.TraceWater(query, Vector3.Zero, identity, spans, contacts);
                long ticks = Stopwatch.GetTimestamp() - before;
                Assert.Equal(MovementAvailability.Known, result.Availability);
                return ticks;
            }
        }
        finally
        {
            TileMediumSampler.BypassTraceMemoForTests = priorBypass;
            TileWaterQueryWork.Enabled = priorEnabled;
        }
    }

    void ReportDryShortcutAgainstExactResultLookup()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") != "0" ||
            !TileWaterFloatingPointState.TryRead(out _))
        {
            output.WriteLine("Dry lookup price requires tiering disabled and a supported controls reader.");
            return;
        }
        using var counters = FrozenWaterCounterScope.Suppress();
        var field = MovementWaterFieldTests.Field(TileWorldPhysicsTestData.FlatWorld());
        var geometry = field.CaptureTraceForTests();
        var index = new TileWaterFlatIndex(geometry.Columns, geometry.Boundaries, geometry.Exterior);
        var table = new TileWaterTraceResultMemo.Table();
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var query = TileWaterTraceMemoResultTests.Query();
        var identity = MovementWaterFieldTests.Identity;
        var dry = Evaluate(query);
        Assert.Equal(MovementAvailability.Known, dry.Availability);
        Assert.Equal(1, dry.SpansWritten);
        Assert.Equal(0, dry.ContactsWritten);
        for (int i = 0; i < 8; i++)
        {
            var key = Key(query) with { Dy = i };
            table.Store(key, dry, spans, contacts);
        }
        var miss = new MovementMediumSweepQuery(query.Body, Vector3.UnitX);
        Assert.True(Lookup(query));
        Assert.False(Lookup(miss));
        const int samples = 2048;
        int hits = 0;
        long start = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++) if (Lookup(query)) hits++;
        long hitTicks = Stopwatch.GetTimestamp() - start;
        Assert.Equal(samples, hits);
        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++) _ = Evaluate(query);
        long dryTicks = Stopwatch.GetTimestamp() - start;
        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < samples; i++)
        {
            if (Lookup(miss)) throw new InvalidOperationException();
            _ = Evaluate(miss);
        }
        long missTicks = Stopwatch.GetTimestamp() - start;
        double nanoseconds = 1e9 / Stopwatch.Frequency / samples;
        output.WriteLine($"DRY_RESULT_LOOKUP samples={samples}, direct-kernel-ns={dryTicks * nanoseconds:R}, hit-ns={hitTicks * nanoseconds:R}, miss-plus-kernel-ns={missTicks * nanoseconds:R}, utc={DateTime.UtcNow:O}");

        TileWaterTraceMemo.Key Key(MovementMediumSweepQuery q)
        {
            if (!TileWaterFloatingPointState.TryRead(out ulong token)) throw new InvalidOperationException();
            return new(q, Vector3.Zero, spans.Length, contacts.Length, token);
        }
        bool Lookup(MovementMediumSweepQuery q) => table.TryRead(Key(q), q.Body.CurrentSpace, identity, spans, contacts, out _);
        MovementCoverageResult Evaluate(MovementMediumSweepQuery q) => TileWaterColumnCoverage.Trace(geometry.Columns,
            geometry.Boundaries, geometry.Exterior, q, Vector3.Zero, identity, spans, contacts, index);
    }

    static MovementMediumSweepQuery Query(int index)
    {
        var query = TileWaterTraceMemoObservationTests.Query(MovementWaterFieldTests.Space);
        return new(new(query.Body.Centre + new Vector3(0, index * .00001f, 0), query.Body.Radius,
            query.Body.HalfHeight, query.Body.CurrentSpace, null), query.Delta);
    }
}
