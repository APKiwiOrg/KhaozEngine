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
        if (!TileWaterFloatingPointState.TryRead(out _, out _))
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
                Assert.True(TileWaterFloatingPointState.TryRead(out ulong controls, out ulong status));
                var key = new TileWaterTraceMemo.Key(query, Vector3.Zero, spans.Length, contacts.Length, controls);
                table.Store(key, result, spans, contacts, status);
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

            bool Lookup(in MovementMediumSweepQuery query)
            {
                if (!TileWaterFloatingPointState.TryRead(out ulong controls, out ulong status)) return false;
                var key = new TileWaterTraceMemo.Key(query, Vector3.Zero, spans.Length, contacts.Length, controls);
                return table.TryRead(key, query.Body.CurrentSpace, identity, status, spans, contacts, out _);
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

    static MovementMediumSweepQuery Query(int index)
    {
        var query = TileWaterTraceMemoObservationTests.Query(MovementWaterFieldTests.Space);
        return new(new(query.Body.Centre + new Vector3(0, index * .00001f, 0), query.Body.Radius,
            query.Body.HalfHeight, query.Body.CurrentSpace, null), query.Delta);
    }
}
