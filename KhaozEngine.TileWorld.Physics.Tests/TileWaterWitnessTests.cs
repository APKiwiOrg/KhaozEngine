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
    [Fact]
    public void ALocatedWitnessThatCannotMeetTheMillimetreBudgetRefusesWithoutPublishing()
    {
        const int tileX = 1000000;
        var doc = TileWorldPhysicsTestData.FlatWorld(RegionCoord.Of(tileX, -1));
        doc.SetUnderlay(tileX, -1, 0, TileWorldPhysicsTestData.Water);
        doc.SetCornerHeightCm(tileX, -1, 0, -200);
        doc.SetCornerHeightCm(tileX + 1, -1, 0, 2);
        var field = MovementWaterFieldTests.Field(doc);
        var query = new MovementMediumSweepQuery(new(new(tileX + 0.25f, 1, 0.5f), 0.1f, 0.25f, MovementWaterFieldTests.Space, null), new(0.2f, -4, 0));
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var result = field.TraceWater(query, Vector3.Zero, MovementWaterFieldTests.Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Unresolved, result.Availability);
        Assert.Equal(0, result.SpansWritten);
        Assert.Equal(0, result.ContactsWritten);
        Assert.All(spans, s => Assert.Equal(default, s));
        Assert.All(contacts, c => Assert.Equal(default, c));
    }
}
