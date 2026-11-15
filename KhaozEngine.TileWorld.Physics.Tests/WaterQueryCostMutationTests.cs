using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;
using Xunit.Sdk;

namespace KhaozEngine.Tests.TileWorld.Physics;

[Trait("Category", "WaterKernelContract")]
public sealed class WaterQueryCostMutationTests
{
    [Theory]
    [InlineData("cold")]
    [InlineData("warm")]
    [InlineData("large")]
    [InlineData("moving")]
    public void EachControlRejectsADeliberatelyWrongEvaluation(string control)
    {
        // #1756. These are comparator/oracle controls. No future cache or cache-key implementation is assumed.
        var geometry = control == "large" ? WaterKernelTraceFixture.RepeatedFaces(64, wet: true) : null;
        var field = geometry is null ? MovementWaterFieldTests.Field(GrazingWaterBank.Document()) :
            TileMediumSampler.FromTraceForTests(geometry, "water-world");
        var frozen = new FrozenWaterReference(field.CaptureTraceForTests());
        var query = geometry is null ? TileWaterTraceMemoObservationTests.Query(MovementWaterFieldTests.Space) :
            WaterKernelTraceFixture.Query();
        field.ResetTraceMemoForTests();
        var original = Read(field, frozen, query);
        original.Equal(); // A positive control through the real production evaluator must pass first.
        Assert.Equal(0, original.Hits);
        if (control == "warm")
        {
            var id = new MovementQueryIdentity("fresh-closure", 2, "fresh-scope");
            var warm = Read(field, frozen, query, id);
            warm.Equal();
            Assert.Equal(TileMediumSampler.TryReadTraceControls(out _) ? 1 : 0, warm.Hits);
            // A wrong replay keeps the previous caller's identity, even though the geometry is identical.
            warm.Actual = WithIdentity(warm.Actual, original.Actual.Identity);
            Assert.ThrowsAny<XunitException>(warm.Equal);
        }
        else if (control == "moving")
        {
            var moved = new MovementMediumSweepQuery(new(query.Body.Centre + new Vector3(0.03f, 0.001f, 0),
                query.Body.Radius, query.Body.HalfHeight, query.Body.CurrentSpace, null), new(0.07f, 0, -0.02f));
            var next = Read(field, frozen, moved);
            next.Equal();
            Assert.Equal(0, next.Hits);
            // A wrong exact-result evaluator returns the previous query's output at a new position and delta.
            Assert.ThrowsAny<XunitException>(() => WaterCoverageComparison.Equal(next.Expected, original.Actual,
                next.ExpectedSpans, original.ActualSpans, next.ExpectedContacts, original.ActualContacts));
        }
        else if (control == "large")
        {
            Assert.True(original.Actual.ContactsWritten > TileWaterTraceMemo.ContactLimit);
            // Truncation after the old small-table limit loses a real, published contact.
            original.ActualContacts[original.Actual.ContactsWritten - 1] = FrozenWaterComparison.ContactSentinel;
            Assert.ThrowsAny<XunitException>(original.Equal);
        }
        else
        {
            // A cold evaluator changes one low bit in an unwritten element inside the supplied caller slice.
            int tail = original.Actual.SpansWritten;
            Assert.True(tail < 64);
            var sentinel = original.ActualSpans[tail];
            original.ActualSpans[tail] = new(MathF.BitIncrement(sentinel.EnterFraction), sentinel.ExitFraction,
                sentinel.ContactStart, sentinel.ContactCount, sentinel.HasDryCoverage);
            Assert.ThrowsAny<XunitException>(original.Equal);
        }
    }

    static Observation Read(TileMediumSampler field, FrozenWaterReference frozen, MovementMediumSweepQuery query,
        MovementQueryIdentity? identity = null)
    {
        var o = new Observation();
        var id = identity ?? MovementWaterFieldTests.Identity;
        o.Expected = frozen.Trace(query, Vector3.Zero, id, o.ExpectedSpans.AsSpan(0, 64), o.ExpectedContacts.AsSpan(0, 256));
        bool enabled = TileWaterQueryWork.Enabled, bypass = TileMediumSampler.BypassTraceMemoForTests;
        TileWaterQueryWork.Reset();
        TileMediumSampler.BypassTraceMemoForTests = false;
        try
        {
            o.Actual = field.TraceWater(query, Vector3.Zero, id, o.ActualSpans.AsSpan(0, 64), o.ActualContacts.AsSpan(0, 256));
            o.Hits = TileWaterQueryWork.MemoHits;
        }
        finally
        {
            TileWaterQueryWork.Enabled = enabled;
            TileMediumSampler.BypassTraceMemoForTests = bypass;
        }
        Assert.Equal(MovementAvailability.Known, o.Actual.Availability);
        return o;
    }

    static MovementCoverageResult WithIdentity(MovementCoverageResult r, MovementQueryIdentity id) =>
        new(r.Availability, r.SpansWritten, r.RequiredSpanCapacity, r.ContactsWritten, r.RequiredContactCapacity,
            r.CertifiedErrorMetres, id);

    sealed class Observation
    {
        internal MovementCoverageResult Expected, Actual;
        internal int Hits;
        internal readonly MovementCoverageSpan[] ExpectedSpans = Enumerable.Repeat(FrozenWaterComparison.SpanSentinel, 66).ToArray();
        internal readonly MovementCoverageSpan[] ActualSpans = Enumerable.Repeat(FrozenWaterComparison.SpanSentinel, 66).ToArray();
        internal readonly MovementDomainContact[] ExpectedContacts = Enumerable.Repeat(FrozenWaterComparison.ContactSentinel, 258).ToArray();
        internal readonly MovementDomainContact[] ActualContacts = Enumerable.Repeat(FrozenWaterComparison.ContactSentinel, 258).ToArray();
        internal void Equal() => WaterCoverageComparison.Equal(Expected, Actual, ExpectedSpans, ActualSpans, ExpectedContacts, ActualContacts);
    }
}
