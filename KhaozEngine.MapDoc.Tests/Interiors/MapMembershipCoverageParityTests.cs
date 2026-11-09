using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.Primitives;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Interiors;

[Collection("AllocSensitive")]
public sealed class MapMembershipCoverageParityTests
{
    [Theory]
    [InlineData("cave")]
    [InlineData("joined")]
    [InlineData("indoor")]
    [InlineData("mixed")]
    [InlineData("tagged")]
    [InlineData("hole")]
    [InlineData("untagged")]
    [InlineData("legacy-cave")]
    [InlineData("upper-tag")]
    [InlineData("foreign-lattice")]
    public void SparsePreparedInputs_MatchColdCavesMixedBoundsAndLegacyFailures(string variant)
    {
        MapDocument document = variant switch
        {
            "cave" => CaveFixtures.RampChamberShaft(),
            "joined" => CaveFixtures.ShaftJoinedToLowerChamber(),
            "indoor" => CaveFixtures.IndoorRow(),
            "mixed" => MixedResolutionFixtures.Converted(),
            _ => LegacyExteriorFixtures.Row(variant == "legacy-cave" ? "cave" : variant)
        };
        var view = CaveFixtures.View(document);
        var records = MapSpaceGeometry.Records(view).ToArray();
        int roots = records.OfType<MapSpaceFootprint>().Count();
        var demand = new MapMembershipPreparation(0, 0, 0, records.Length, roots, 0, System.Math.Max(1, roots));
        var scratch = new MapSpaceMembershipScratch(view, demand);
        var membership = new MapSpaceMembership(view);
        Vector3[] positions = [new(.5f, 1, -.5f), new(1.5f, 5, 1.5f), new(2.5f, 5.5f, 4.5f),
            new(4, 8.5f, 3), new(3.5f, 4, 5.5f), new(3.5f, 3.99f, 5.5f),
            new(3.5f, -15, 5.5f), new(3.5f, -15.01f, 5.5f), new(1.5f, 3, -.5f),
            new(3.5f, 5, -.5f), new(0, 1, 0), new(float.Epsilon, 1, -1)];
        var points = positions.Select(p => new MapFramePoint(WorldFrame.Origin, p)).ToArray();
        var expected = points.Select(membership.Query).ToArray();
        foreach (var point in points) _ = membership.Query(point, scratch);
        var observations = new MapMembershipObservation[points.Length];
        long scans = MapSpaceGeometry.ThreadRecordScans, contexts = MapBoundFaceContext.ThreadPreparations;
        long allocated = AllocationMeasurement.Measure("sparse membership fixture parity", () =>
        {
            for (int i = 0; i < points.Length; i++) observations[i] = membership.Query(points[i], scratch);
        });
        Assert.Equal(0, allocated);
        Assert.Equal(scans, MapSpaceGeometry.ThreadRecordScans);
        Assert.Equal(contexts, MapBoundFaceContext.ThreadPreparations);
        for (int i = 0; i < points.Length; i++)
        {
            Assert.Equal(expected[i].Status, observations[i].Status);
            Assert.Equal(expected[i].SpaceId, observations[i].SpaceId);
            Assert.Equal(expected[i].ExactPoint, observations[i].ExactPoint);
            Assert.Same(expected[i].Witness, observations[i].Witness);
        }
    }
}
