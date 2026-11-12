using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using KhaozEngine.Tests.WaterPrismCensus;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
public sealed class TileWaterPrismCensusTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("bank-stand")]
    [InlineData("bank-along")]
    [InlineData("bank-across")]
    [InlineData("swim-entry")]
    [InlineData("deep-swim")]
    [InlineData("deep-tread")]
    [InlineData("flat-neighbour")]
    [InlineData("flat-control")]
    [InlineData("dry-control")]
    public void ContactAuthorityStepPinsRepresentativePrismWorkAndCompleteCoverage(string name)
    {
        // #1755. Fixed diagnostic counts make changes in exact-solve work reviewable.
        using var baselineStream = typeof(TileWaterPrismCensusTests).Assembly.GetManifestResourceStream(
            "KhaozEngine.Tests.Fixtures.WaterPrismCensusBaseline.json");
        Assert.NotNull(baselineStream);
        using var baseline = JsonDocument.Parse(baselineStream);
        var expectedCase = baseline.RootElement.GetProperty(name);
        Assert.Equal(baseline.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal),
            WaterPrismScenario.Names.Order(StringComparer.Ordinal));
        using var scene = new WaterPrismScenario(name);
        var snapshot = scene.Colliders.Medium.CaptureTraceForTests();
        var frozen = new FrozenWaterReference(snapshot);
        var records = new List<(MovementMediumSweepQuery Query, MovementCoverageResult Result,
            MovementCoverageSpan[] Spans, MovementDomainContact[] Contacts)>();
        scene.Field.Recorded = (q, r, s, c) => records.Add((q, r, s, c));
#if WATER_PRISM_CENSUS
        var rows = new List<WaterPrismMeasurement.TraceRow>();
        WaterPrismMeasurement.Reset(rows);
        WaterPrismMeasurement.Geometry = snapshot;
        WaterPrismMeasurement.Stage = "authority";
        WaterPrismMeasurement.Tick = 0;
        try
        {
#endif
        var next = scene.Step(scene.Basis, scene.Command);
        if (name == "swim-entry")
        {
            Assert.False(scene.Basis.Swimming);
            Assert.True(next.Swimming);
        }
        if (name is "bank-stand" or "deep-tread") Assert.Equal(scene.Basis.Position, next.Position);
#if WATER_PRISM_CENSUS
            WaterPrismMeasurement.Stage = "replay";
#endif
        var replay = scene.Step(scene.Basis, scene.Command);
        Assert.Equal(next, replay);
#if WATER_PRISM_CENSUS
            WaterPrismMeasurement.Stage = "authority";
            WaterPrismMeasurement.Tick = 1;
#endif
        var following = scene.Step(next, scene.Command);
        Assert.Equal(name is "swim-entry" or "deep-swim" or "deep-tread" or "flat-neighbour" or "flat-control", following.Swimming);
#if WATER_PRISM_CENSUS
        }
        finally
        {
            WaterPrismMeasurement.Rows = null;
            WaterPrismMeasurement.Geometry = null;
        }
        output.WriteLine("CENSUS " + JsonSerializer.Serialize(new { name, rows }));
#endif
        Assert.NotEmpty(records);
        using var digest = new WaterCoverageDigest();
        foreach (var record in records)
        {
            var spans = new MovementCoverageSpan[64];
            var contacts = new MovementDomainContact[256];
            var expected = frozen.Trace(record.Query, Vector3.Zero, record.Result.Identity, spans, contacts);
            WaterCoverageComparison.Equal(expected, record.Result, spans[..expected.SpansWritten], record.Spans,
                contacts[..expected.ContactsWritten], record.Contacts);
            digest.Add(record.Query, record.Result, record.Spans, record.Contacts);
        }
        output.WriteLine($"DIGEST {name} {digest.Digest()} traces={records.Count}");
#if WATER_PRISM_CENSUS
        Assert.Equal(JsonSerializer.Serialize(expectedCase.GetProperty("Census")), Snapshot(rows));
#endif
        Assert.Equal(expectedCase.GetProperty("Digest").GetString(), digest.Digest());
        output.WriteLine($"CORPUS case={name}, recorded={records.Count}");
    }

#if WATER_PRISM_CENSUS
    static string Snapshot(List<WaterPrismMeasurement.TraceRow> rows)
    {
        var first = rows.Where(r => r.Stage == "authority" && r.Tick == 0).ToArray();
        var inputs = first.SelectMany(r => r.Inputs).ToArray();
        var known = inputs.ToHashSet(StringComparer.Ordinal);
        return JsonSerializer.Serialize(new
        {
            Traces = rows.Select(r => new
            {
                r.Stage, r.Tick, r.Work, Intersections = r.Inputs.Count,
                WithinTraceRepeats = r.Inputs.Count - r.Inputs.Distinct(StringComparer.Ordinal).Count(),
                r.RejectedPrisms, r.RejectedFaces, r.RejectedIntersections, r.RejectedBases, r.RejectedDots,
                r.Spans, r.Contacts, r.Availability, r.FullyDry, r.FullySubmerged, r.PrismKernel,
            }),
            Repeats = new
            {
                WithinTraces = first.Sum(r => r.Inputs.Count - r.Inputs.Distinct(StringComparer.Ordinal).Count()),
                WithinStep = inputs.Length - known.Count,
                TraceQueries = first.Length - first.Select(r => r.QueryHash).Distinct(StringComparer.Ordinal).Count(),
                ReplayHits = rows.Where(r => r.Stage == "replay").SelectMany(r => r.Inputs).Count(known.Contains),
                NextTickHits = rows.Where(r => r.Stage == "authority" && r.Tick == 1).SelectMany(r => r.Inputs).Count(known.Contains),
            },
        });
    }
#endif
}
