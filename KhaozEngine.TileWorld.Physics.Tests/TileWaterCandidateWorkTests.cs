using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterCandidateWorkTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, 4)]
    [InlineData(true, 80)]
    public void DistantWaterCertifiesDryWithoutGeometryOrGlobalNonuniformScan(bool interiorGap, int nodes)
    {
        // #1728. A dry query outside the field must cost bounded tree rejection,
        // independent of all the captured flat columns and nonuniform rim tiles.
        var field = new TileMediumSampler(Fixtures.TileWaterIslands.Create(), TileWorldPhysicsTestData.Catalogs(),
            new[] { new RegionCoord(0, -1) }, "water-world");
        var spans = new MovementCoverageSpan[64];
        TileWaterQueryWork.Reset();
        try
        {
            var result = field.TraceWater(new(new(interiorGap ? new(3, -1, 2.5f) : new(-8, -1, -8), 0.25f, 0.75f,
                MovementWaterFieldTests.Space, null), interiorGap ? new(.1f, 0, .1f) : new(1, 0, 1)), Vector3.Zero,
                MovementWaterFieldTests.Identity, spans, new MovementDomainContact[256]);
            Assert.Equal(MovementAvailability.Known, result.Availability);
            Assert.Equal(new(0, 1, 0, 0, true), Assert.Single(spans[..result.SpansWritten]));
            output.WriteLine($"intersections={TileWaterQueryWork.Intersections}, nonuniform={TileWaterQueryWork.NonuniformVisits}, nodes={TileWaterQueryWork.IndexNodes}");
            Assert.Equal(0, TileWaterQueryWork.Intersections);
            Assert.Equal(0, TileWaterQueryWork.NonuniformVisits);
            Assert.InRange(TileWaterQueryWork.IndexNodes, 1, nodes);
        }
        finally { TileWaterQueryWork.Enabled = false; }
    }

    [Fact]
    public void FlatInteriorIntersectsOnlyItsLocalColumn()
    {
        var field = new TileMediumSampler(Fixtures.TileWaterIslands.Create(), TileWorldPhysicsTestData.Catalogs(),
            new[] { new RegionCoord(0, -1) }, "water-world");
        var captured = field.CaptureTraceForTests();
        var query = new MovementMediumSweepQuery(new(new(1.5f, -1, .5f), .25f, .75f,
            MovementWaterFieldTests.Space, null), new(.05f, 0, .05f));
        var expectedSpans = new MovementCoverageSpan[64];
        var actualSpans = new MovementCoverageSpan[64];
        var expectedContacts = new MovementDomainContact[256];
        var actualContacts = new MovementDomainContact[256];
        TileWaterQueryWork.Reset();
        try
        {
            var expected = Fixtures.WaterTraceReference.Trace(captured, query, Vector3.Zero,
                MovementWaterFieldTests.Identity, expectedSpans, expectedContacts);
            int before = TileWaterQueryWork.Intersections;
            Assert.True(before > 1024);
            TileWaterQueryWork.Reset();
            var actual = field.TraceWater(query, Vector3.Zero, MovementWaterFieldTests.Identity,
                actualSpans, actualContacts);
            output.WriteLine($"intersections before={before}, after={TileWaterQueryWork.Intersections}, nodes={TileWaterQueryWork.IndexNodes}");
            Assert.Equal(expected, actual);
            Assert.Equal(expectedSpans, actualSpans);
            Assert.Equal(expectedContacts, actualContacts);
            Assert.True(actual.ContactsWritten > 0);
            Assert.InRange(TileWaterQueryWork.Intersections, 1, 8);
        }
        finally { TileWaterQueryWork.Enabled = false; }
    }

}
