using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterWitnessTests
{
    [Fact]
    public void APointOutsideTheFiniteExtentCannotAliasAnIntegerMinimumTile()
    {
        var coord = RegionCoord.Of(int.MinValue, -1);
        var doc = TileWorldPhysicsTestData.FlatWorld(coord);
        doc.SetUnderlay(int.MinValue, -1, 0, TileWorldPhysicsTestData.Water);
        doc.SetCornerHeightCm(int.MinValue, -1, 0, -200);
        doc.SetCornerHeightCm(int.MinValue + 1, -1, 0, 2);
        var field = new TileMediumSampler(doc, TileWorldPhysicsTestData.Catalogs(), new[] { coord }, "water-world");
        var point = field.SampleCentreWater(new(new(float.MaxValue, -0.5f, 0.5f), 0.1f, 0.25f, MovementWaterFieldTests.Space, null), Vector3.Zero);
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.False(point.InWater);
    }
    [Theory]
    [InlineData(0, MovementAvailability.Known)]
    [InlineData(1000000, MovementAvailability.PrecisionExceeded)]
    public void ALocatedWitnessThatCannotMeetTheMillimetreBudgetRefusesWithoutPublishing(int tileX, MovementAvailability expected)
    {
        var doc = TileWorldPhysicsTestData.FlatWorld(RegionCoord.Of(tileX, -1));
        doc.SetUnderlay(tileX, -1, 0, TileWorldPhysicsTestData.Water);
        doc.SetCornerHeightCm(tileX, -1, 0, -200);
        doc.SetCornerHeightCm(tileX + 1, -1, 0, 2);
        var field = MovementWaterFieldTests.Field(doc);
        var query = new MovementMediumSweepQuery(new(new(tileX + 0.25f, 1, 0.5f), 0.1f, 0.25f, MovementWaterFieldTests.Space, null), new(0.2f, -4, 0));
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var result = field.TraceWater(query, Vector3.Zero, MovementWaterFieldTests.Identity, spans, contacts);
        // At free-surface entry the lower capsule cap reaches Y=0 after 3/16 of the path.
        // Its column X must round to binary32. At the large address that actual displacement
        // exceeds the budget, independent of the producer's own error calculation.
        double exactX = query.Body.Centre.X + (double)query.Delta.X * (3d / 16);
        double rounding = System.Math.Abs(exactX - (float)exactX);
        Assert.Equal(expected == MovementAvailability.Known, rounding <= MovementQueryLease.CoverageSkinMetres);
        Assert.Equal(expected, result.Availability);
        if (expected == MovementAvailability.Known)
        {
            Assert.True(result.ContactsWritten > 0);
            return;
        }
        Assert.Equal(0, result.SpansWritten);
        Assert.Equal(0, result.ContactsWritten);
        Assert.All(spans, s => Assert.Equal(default, s));
        Assert.All(contacts, c => Assert.Equal(default, c));
    }
}
