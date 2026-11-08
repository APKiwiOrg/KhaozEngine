using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterIndexEquivalenceTests
{
    [Theory]
    [InlineData(1728, 0)]
    [InlineData(1729, 1048576)]
    [InlineData(1730, -4194304)]
    public void SeededFlatUnionResultsMatchTheFrozenReferenceExactly(int seed, float offset)
    {
        // #1728. Original column and face ordering, error certificates, dry seams and
        // atomic refusals must survive conservative candidate pruning without tolerances.
        var random = new Random(seed);
        var columns = Enumerable.Range(0, 40).Select(i => Column(i / 3,
            new(offset + 2 * (i % 8), -4 + i % 3, offset + 2 * (i / 8)),
            new(offset + 2 * (i % 8) + 2, i % 2, offset + 2 * (i / 8) + 2))).ToArray();
        var boundaries = TileWaterBoundary.Build(columns, true);
        var exterior = TileWaterBoundary.Build(columns, false);
        var index = new TileWaterFlatIndex(columns, boundaries, exterior);
        Vector3 origin = new(offset, offset == 0 ? 0 : 1024, offset);
        for (int i = 0; i < 256; i++)
        {
            float radius = i % 3 == 0 ? .125f : i % 3 == 1 ? .25f : .5f;
            Vector3 centre = i < 40 ? new(2 * (i % 8) - radius, -1, 2 * (i / 8) + 1) :
                new(Next(-3, 19), Next(-6, 3), Next(-3, 13));
            centre.Y -= origin.Y;
            Vector3 delta = i % 4 == 0 ? Vector3.Zero : new(Next(-6, 6), Next(-3, 3), Next(-6, 6));
            if (i % 8 == 1) delta = new(1e-15f, 0, delta.Z);
            var query = new MovementMediumSweepQuery(new(centre, radius, .75f,
                MovementWaterFieldTests.Space, null), delta);
            Compare(columns, boundaries, exterior, index, query, origin, i % 17 == 0 ? 0 : 64, i % 19 == 0 ? 0 : 256);
        }
        float Next(float min, float max) => min + (max - min) * random.NextSingle();
    }

    [Theory]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(2050)]
    public void CandidateOverflowRetainsTheOriginalContactCapacityOutcome(int count)
    {
        var columns = Enumerable.Range(0, count).Select(i => Column(i, new(-10), new(10))).ToArray();
        var boundaries = TileWaterBoundary.Build(columns, true);
        var exterior = TileWaterBoundary.Build(columns, false);
        var query = new MovementMediumSweepQuery(new(Vector3.Zero, .125f, .25f,
            MovementWaterFieldTests.Space, null), Vector3.Zero);
        var result = Compare(columns, boundaries, exterior, new(columns, boundaries, exterior), query, Vector3.Zero, 64, 256);
        Assert.Equal(count == 256 ? MovementAvailability.Known : MovementAvailability.CapacityExceeded, result.Availability);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    public void DirectedSpanCapacityAndBoundaryOrderMatchAtTheLimit(int count)
    {
        var columns = Enumerable.Range(0, count).Select(i => Column(i,
            new(i == 16 ? 127.875f : 2 * i, -10, -10),
            new(i == 16 ? 129 : 2 * i + 1, 10, 10))).ToArray();
        var boundaries = TileWaterBoundary.Build(columns, true);
        var exterior = TileWaterBoundary.Build(columns, false);
        var query = new MovementMediumSweepQuery(new(Vector3.Zero, .125f, .25f,
            MovementWaterFieldTests.Space, null), new(128, 0, 0));
        var result = Compare(columns, boundaries, exterior, new(columns, boundaries, exterior), query, Vector3.Zero, 64, 256);
        Assert.Equal(count == 16 ? MovementAvailability.Known : MovementAvailability.CapacityExceeded, result.Availability);
    }

    [Theory]
    [InlineData("query", MovementAvailability.Invalid)]
    [InlineData("identity", MovementAvailability.Invalid)]
    [InlineData("world", MovementAvailability.Invalid)]
    [InlineData("precision", MovementAvailability.PrecisionExceeded)]
    public void InvalidInputsAndLocatedPrecisionRefusalRetainTheirExactOutcome(string kind, MovementAvailability status)
    {
        var columns = new[] { Column(0, new(1000000, -2, 0), new(1000001, 0, 1)) };
        if (kind == "world") columns[0] = columns[0] with { Domain = new("foreign", "body") };
        var boundaries = TileWaterBoundary.Build(columns, true);
        var exterior = TileWaterBoundary.Build(columns, false);
        var query = kind == "query" ? default : kind == "precision" ? new MovementMediumSweepQuery(
            new(new(1000000.25f, 1, .5f), .1f, .25f, MovementWaterFieldTests.Space, null), new(.2f, -4, 0)) :
            new(new(new(1000000.5f, -1, .5f), .1f, .25f, MovementWaterFieldTests.Space, null), Vector3.Zero);
        var result = Compare(columns, boundaries, exterior, new(columns, boundaries, exterior), query,
            Vector3.Zero, 64, 256, kind == "identity" ? default(MovementQueryIdentity) : MovementWaterFieldTests.Identity);
        Assert.Equal(status, result.Availability);
    }

    internal static TileWaterColumn Column(int id, Vector3 min, Vector3 max) => new(new("water-world", $"body/{id}"), min, max,
        $"{id}/min-x", $"{id}/max-x", $"{id}/min-z", $"{id}/max-z", $"{id}/bed", $"{id}/surface");

    internal static MovementCoverageResult Compare(TileWaterColumn[] columns, TileWaterBoundary[] boundaries,
        TileWaterBoundary[] exterior, TileWaterFlatIndex index, in MovementMediumSweepQuery query,
        Vector3 origin, int spanCapacity, int contactCapacity, MovementQueryIdentity? identity = null)
    {
        var expectedSpans = Enumerable.Repeat(new MovementCoverageSpan(.9f, 1, 0, 0, true), 64).ToArray();
        var actualSpans = expectedSpans.ToArray();
        var expectedContacts = new MovementDomainContact[256];
        var actualContacts = new MovementDomainContact[256];
        var expected = FlatWaterCoverageReference.Trace(columns, boundaries, exterior, query, origin,
            identity ?? MovementWaterFieldTests.Identity, expectedSpans.AsSpan(0, spanCapacity), expectedContacts.AsSpan(0, contactCapacity));
        var actual = TileWaterColumnCoverage.Trace(columns, boundaries, exterior, query, origin,
            identity ?? MovementWaterFieldTests.Identity, actualSpans.AsSpan(0, spanCapacity), actualContacts.AsSpan(0, contactCapacity), index);
        WaterCoverageComparison.Equal(expected, actual, expectedSpans, actualSpans, expectedContacts, actualContacts);
        return actual;
    }

}
