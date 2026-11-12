using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using R = KhaozEngine.TileWorld.Physics.TileWaterConvexShape.Range;

namespace KhaozEngine.Tests.TileWorld.Physics.Fixtures;

internal static class FrozenWaterComparison
{
    internal static readonly MovementCoverageSpan SpanSentinel = new(0.875f, 0.9375f, 0, 0, true);
    internal static readonly MovementDomainContact ContactSentinel = new(new("sentinel", "domain"),
        new("sentinel", "space"), new(-9, -3, -3, true, "sentinel-bed", "sentinel-surface"),
        new(17, 19), Vector3.UnitX, 0.625f, "sentinel-boundary", 91,
        MovementContactOverlap.Tangent, MovementContactOverlap.Tangent);

    internal static MovementCoverageResult Check(TileMediumSampler actual, FrozenWaterReference frozen,
        MovementMediumSweepQuery query, Vector3 origin = default, int spanCapacity = 64, int contactCapacity = 256,
        MovementQueryIdentity? identity = null, bool warmOnly = false)
    {
        var expectedSpans = Enumerable.Repeat(SpanSentinel, 66).ToArray();
        var actualSpans = expectedSpans.ToArray();
        var expectedContacts = Enumerable.Repeat(ContactSentinel, 258).ToArray();
        var actualContacts = expectedContacts.ToArray();
        var token = identity ?? MovementWaterFieldTests.Identity;
        var expected = frozen.Trace(query, origin, token, expectedSpans.AsSpan(0, spanCapacity), expectedContacts.AsSpan(0, contactCapacity));
        var result = actual.TraceWater(query, origin, token, actualSpans.AsSpan(0, spanCapacity), actualContacts.AsSpan(0, contactCapacity));
        Equal(expected, result, expectedSpans, actualSpans, expectedContacts, actualContacts);
        Assert.All(actualSpans.Skip(result.SpansWritten), value => WaterCoverageComparison.Span(SpanSentinel, value));
        Assert.All(actualContacts.Skip(result.ContactsWritten), value => WaterCoverageComparison.Contact(ContactSentinel, value));
        CheckMemoStates(actual, query, origin, token, expected, expectedSpans, expectedContacts, spanCapacity, contactCapacity, warmOnly);
        return result;
    }

    internal static MovementCoverageResult CheckPrisms(TileWaterTraceTestSnapshot geometry,
        MovementMediumSweepQuery query, int spanCapacity = 64, int contactCapacity = 256)
    {
        var expectedSpans = Enumerable.Repeat(SpanSentinel, 66).ToArray();
        var actualSpans = expectedSpans.ToArray();
        var expectedContacts = Enumerable.Repeat(ContactSentinel, 258).ToArray();
        var actualContacts = expectedContacts.ToArray();
        var frozen = new FrozenWaterReference(geometry);
        var identity = MovementWaterFieldTests.Identity;
        var expected = frozen.TracePrisms(query, Vector3.Zero, identity,
            expectedSpans.AsSpan(0, spanCapacity), expectedContacts.AsSpan(0, contactCapacity));
        var actual = TileWaterPrismCoverage.Trace(geometry.Prisms, geometry.Faces, geometry.Ground, geometry.PointIndex,
            geometry.PrismStarts, geometry.FaceStarts, query, Vector3.Zero, identity,
            actualSpans.AsSpan(0, spanCapacity), actualContacts.AsSpan(0, contactCapacity));
        Equal(expected, actual, expectedSpans, actualSpans, expectedContacts, actualContacts);
        Assert.All(actualSpans.Skip(actual.SpansWritten), value => WaterCoverageComparison.Span(SpanSentinel, value));
        Assert.All(actualContacts.Skip(actual.ContactsWritten), value => WaterCoverageComparison.Contact(ContactSentinel, value));
        var sampler = TileMediumSampler.FromTraceForTests(geometry, query.Body.CurrentSpace.WorldId);
        CheckMemoStates(sampler, query, Vector3.Zero, identity, expected, expectedSpans, expectedContacts, spanCapacity, contactCapacity);
        return actual;
    }

    static void CheckMemoStates(TileMediumSampler sampler, MovementMediumSweepQuery query, Vector3 origin,
        MovementQueryIdentity identity, MovementCoverageResult expected, MovementCoverageSpan[] expectedSpans,
        MovementDomainContact[] expectedContacts, int spanCapacity, int contactCapacity, bool warmOnly = false)
    {
        bool bypass = TileMediumSampler.BypassTraceMemoForTests;
        try
        {
            if (!warmOnly)
            {
                TileMediumSampler.BypassTraceMemoForTests = true;
                Read();
                sampler.ResetTraceMemoForTests();
            }
            TileMediumSampler.BypassTraceMemoForTests = false;
            Read(); // Cold table.
            Read(); // First repeated warm read, including cached refusals.
            Read(); // A hit must not corrupt the stored prefixes or caller suffixes.
        }
        finally { TileMediumSampler.BypassTraceMemoForTests = bypass; }

        void Read()
        {
            var spans = Enumerable.Repeat(SpanSentinel, expectedSpans.Length).ToArray();
            var contacts = Enumerable.Repeat(ContactSentinel, expectedContacts.Length).ToArray();
            var result = sampler.TraceWater(query, origin, identity, spans.AsSpan(0, spanCapacity), contacts.AsSpan(0, contactCapacity));
            Equal(expected, result, expectedSpans, spans, expectedContacts, contacts);
        }
    }

    internal static void Equal(MovementCoverageResult expected, MovementCoverageResult actual,
        MovementCoverageSpan[] expectedSpans, MovementCoverageSpan[] actualSpans,
        MovementDomainContact[] expectedContacts, MovementDomainContact[] actualContacts) =>
        WaterCoverageComparison.Equal(expected, actual, expectedSpans, actualSpans, expectedContacts, actualContacts);

    internal static void Range(FrozenTileWaterConvexShape.Range expected, R actual)
    {
        Assert.Equal(expected.Resolved, actual.Resolved);
        Assert.Equal(expected.Hit, actual.Hit);
        double[] a = [expected.Enter, expected.Exit, expected.MinimumSquared, expected.Error,
            expected.WitnessStart.X.Low, expected.WitnessStart.X.High, expected.WitnessStart.Y.Low, expected.WitnessStart.Y.High,
            expected.WitnessStart.Z.Low, expected.WitnessStart.Z.High, expected.WitnessDrive.X.Low, expected.WitnessDrive.X.High,
            expected.WitnessDrive.Y.Low, expected.WitnessDrive.Y.High, expected.WitnessDrive.Z.Low, expected.WitnessDrive.Z.High];
        double[] b = [actual.Enter, actual.Exit, actual.MinimumSquared, actual.Error,
            actual.WitnessStart.X.Low, actual.WitnessStart.X.High, actual.WitnessStart.Y.Low, actual.WitnessStart.Y.High,
            actual.WitnessStart.Z.Low, actual.WitnessStart.Z.High, actual.WitnessDrive.X.Low, actual.WitnessDrive.X.High,
            actual.WitnessDrive.Y.Low, actual.WitnessDrive.Y.High, actual.WitnessDrive.Z.Low, actual.WitnessDrive.Z.High];
        Assert.Equal(a.Select(BitConverter.DoubleToInt64Bits), b.Select(BitConverter.DoubleToInt64Bits));
    }
}
