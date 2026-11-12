using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using KhaozEngine.Tests.WaterPrismCensus;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
public sealed class WaterKernelMemoIntegrationTests
{
    [Fact]
    public void EveryWorkCounterIsResetBeforeTheNextObservation()
    {
        TileWaterQueryWork.Intersections = TileWaterQueryWork.NonuniformVisits = TileWaterQueryWork.IndexNodes = 61;
        TileWaterQueryWork.ConvexIntersections = TileWaterQueryWork.ShapePreparations = TileWaterQueryWork.LazyCoefficientFills = 62;
        TileWaterQueryWork.BedSeamProofs = TileWaterQueryWork.BedSeamCertificates = 63;
        TileWaterQueryWork.PrismTraces = TileWaterQueryWork.MemoHits = TileWaterQueryWork.MemoMisses = 64;
        try
        {
            TileWaterQueryWork.Reset();
            Assert.All(new[] { TileWaterQueryWork.Intersections, TileWaterQueryWork.NonuniformVisits, TileWaterQueryWork.IndexNodes,
                TileWaterQueryWork.ConvexIntersections, TileWaterQueryWork.ShapePreparations, TileWaterQueryWork.LazyCoefficientFills,
                TileWaterQueryWork.BedSeamProofs, TileWaterQueryWork.BedSeamCertificates,
                TileWaterQueryWork.PrismTraces, TileWaterQueryWork.MemoHits, TileWaterQueryWork.MemoMisses }, value => Assert.Equal(0, value));
        }
        finally { TileWaterQueryWork.Enabled = false; }
    }

    [Fact]
    public void InterleavedMemoHitsAndRefusalsCannotContaminateTheNextMissInAnotherSampler()
    {
        var a = MovementWaterFieldTests.Field(GrazingWaterBank.Document());
        var document = GrazingWaterBank.Document();
        document.SetCornerHeightCm(110, 237, 0, -125);
        var b = MovementWaterFieldTests.Field(document);
        var frozenA = new FrozenWaterReference(a.CaptureTraceForTests());
        var frozenB = new FrozenWaterReference(b.CaptureTraceForTests());
        var q0 = TileWaterTraceMemoObservationTests.Query(MovementWaterFieldTests.Space);
        var q1 = new MovementMediumSweepQuery(new(q0.Body.Centre + new Vector3(.03f, .001f, 0), q0.Body.Radius,
            q0.Body.HalfHeight, q0.Body.CurrentSpace, null), new(.07f, 0, -.02f));
        bool bypass = TileMediumSampler.BypassTraceMemoForTests;
        TileMediumSampler.BypassTraceMemoForTests = false;
        TileWaterQueryWork.Reset();
        try
        {
            Read(a, frozenA, q0, false);
            Read(a, frozenA, q0, true);
            Read(b, frozenB, q0, false);
            Read(b, frozenB, q0, true);
            Read(a, frozenA, q1, false);
            Read(b, frozenB, q0, false, 0, 0);
            Read(b, frozenB, q0, true, 0, 0);
            Read(a, frozenA, q1, true);
            Read(b, frozenB, q1, false);
            Read(a, frozenA, q0, true);
        }
        finally
        {
            TileWaterQueryWork.Enabled = false;
            TileMediumSampler.BypassTraceMemoForTests = bypass;
        }

        void Read(TileMediumSampler field, FrozenWaterReference frozen, MovementMediumSweepQuery query, bool hit,
            int spanCapacity = 64, int contactCapacity = 256)
        {
            var expectedSpans = Enumerable.Repeat(FrozenWaterComparison.SpanSentinel, 66).ToArray();
            var spans = expectedSpans.ToArray();
            var expectedContacts = Enumerable.Repeat(FrozenWaterComparison.ContactSentinel, 258).ToArray();
            var contacts = expectedContacts.ToArray();
            var expected = frozen.Trace(query, Vector3.Zero, MovementWaterFieldTests.Identity,
                expectedSpans.AsSpan(0, spanCapacity), expectedContacts.AsSpan(0, contactCapacity));
            int logical = TileWaterQueryWork.PrismTraces, hits = TileWaterQueryWork.MemoHits,
                misses = TileWaterQueryWork.MemoMisses, preparations = TileWaterQueryWork.ShapePreparations;
            var actual = field.TraceWater(query, Vector3.Zero, MovementWaterFieldTests.Identity,
                spans.AsSpan(0, spanCapacity), contacts.AsSpan(0, contactCapacity));
            WaterCoverageComparison.Equal(expected, actual, expectedSpans, spans, expectedContacts, contacts);
            hit &= TileMediumSampler.TryReadTraceControls(out _);
            Assert.Equal(1, TileWaterQueryWork.PrismTraces - logical);
            Assert.Equal(hit ? 1 : 0, TileWaterQueryWork.MemoHits - hits);
            Assert.Equal(hit ? 0 : 1, TileWaterQueryWork.MemoMisses - misses);
            if (hit) Assert.Equal(preparations, TileWaterQueryWork.ShapePreparations);
            else Assert.True(TileWaterQueryWork.ShapePreparations > preparations);
        }
    }

    [Theory]
    [Trait("Category", "Diagnostic")]
    [InlineData("bank-stand")]
    [InlineData("bank-along")]
    [InlineData("bank-across")]
    [InlineData("swim-entry")]
    [InlineData("deep-swim")]
    [InlineData("deep-tread")]
    [InlineData("flat-neighbour")]
    [InlineData("flat-control")]
    [InlineData("dry-control")]
    public void CensusFixtureMemoOracleAlsoMatchesTheFrozenEvaluator(string name)
    {
        using var scene = new WaterPrismScenario(name);
        var frozen = new FrozenWaterReference(scene.Colliders.Medium.CaptureTraceForTests());
        var queries = new List<MovementMediumSweepQuery>();
        scene.Field.Recorded = (query, _, _, _) => queries.Add(query);
        _ = scene.Step(scene.Basis, scene.Command);
        Assert.NotEmpty(queries);
        scene.Colliders.Medium.ResetTraceMemoForTests();
        foreach (var query in queries)
        {
            var cold = WaterTraceMemoComparison.Check(scene.Colliders.Medium, query);
            var warm = WaterTraceMemoComparison.Check(scene.Colliders.Medium, query);
            if (cold.Logical > 0)
                WaterTraceMemoComparison.Memo(warm, cold.Result.SpansWritten <= TileWaterTraceMemo.SpanLimit &&
                    cold.Result.ContactsWritten <= TileWaterTraceMemo.ContactLimit);
            _ = FrozenWaterComparison.Check(scene.Colliders.Medium, frozen, query);
        }
    }
}
