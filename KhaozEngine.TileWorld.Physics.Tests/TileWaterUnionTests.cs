using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class TileWaterUnionTests
{
    [Fact]
    public void AThinStripWithManyTilesCertifiesAsOneExactUnionWithoutExceedingContactCapacity()
    {
        var doc = TileWorldPhysicsTestData.FlatWorld(new RegionCoord(0, -1));
        doc.TileSize = 0.05f;
        for (int z = -64; z < 0; z++)
            for (int x = 0; x < 64; x++)
            {
                doc.SetCornerHeightCm(x, z, 0, -400);
                if (x == 8) doc.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
            }
        doc.SetCornerHeightCm(8, -64, 0, 2);
        var field = MovementWaterFieldTests.Field(doc);
        var query = new MovementMediumSweepQuery(new(new(0, -3.25f, 1), 0.25f, 0.75f, MovementWaterFieldTests.Space, null), new(1.2f, 0, 0));
        var spans = new MovementCoverageSpan[64];
        var result = field.TraceWater(query, Vector3.Zero, MovementWaterFieldTests.Identity, spans, new MovementDomainContact[256]);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.InRange(result.ContactsWritten, 1, 256);
        Assert.True(spans[0].HasDryCoverage);
        Assert.True(spans[result.SpansWritten - 1].HasDryCoverage);
    }
    [Theory]
    [InlineData(0.7f, false)]
    [InlineData(0.75f, true)]
    public void ConcaveUnionUsesRoundedCapsuleVolumeRatherThanItsBoundingBox(float axis, bool dry)
    {
        var doc = TileWorldPhysicsTestData.FlatWorld(new RegionCoord(0, -1));
        for (int z = -3; z < 0; z++)
            for (int x = 0; x < 3; x++) doc.SetCornerHeightCm(x, z, 0, -200);
        foreach (var tile in new[] { (0, -1), (1, -1), (0, -2) }) doc.SetUnderlay(tile.Item1, tile.Item2, 0, TileWorldPhysicsTestData.Water);
        // The rim stays away from the measured concavity and its deep floor.
        doc.SetCornerHeightCm(0, -2, 0, 2);
        var field = MovementWaterFieldTests.Field(doc);
        var query = new MovementMediumSweepQuery(new(new(axis, -1, axis), 0.4f, 0.5f, MovementWaterFieldTests.Space, null), Vector3.Zero);
        var spans = new MovementCoverageSpan[64];
        var result = field.TraceWater(query, Vector3.Zero, MovementWaterFieldTests.Identity, spans, new MovementDomainContact[256]);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(dry, spans[0].HasDryCoverage);
        Assert.True(result.ContactsWritten > 0);
    }

    [Fact]
    public void AdjacentRegionBodiesRetainDifferentSurfacesAndLocalBedChanges()
    {
        // Keep the original world probes and capsule. A finer lattice authors two actual level
        // strata within its footprint, joined by a short continuous ramp at the region boundary.
        var doc = TileWorldPhysicsTestData.FlatWorld(new(7, -1), new(8, -1), new(7, 0), new(8, 0));
        doc.TileSize = 0.125f;
        for (int z = -7; z <= 0; z++)
            for (int x = 508; x <= 516; x++)
            {
                doc.SetCornerHeightCm(x, z, 0, (short)(x < 512 ? -200 : x == 512 ? -100 : 0));
                if (x < 516 && z < 0) doc.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
            }
        doc.SetCornerHeightCm(508, -7, 0, 2);
        doc.SetCornerHeightCm(516, -7, 0, 102);
        var field = MovementWaterFieldTests.Field(doc);
        MovementWaterPoint Point(float x) => field.SampleCentreWater(new(new(x, 0.1f, 0.5f), 0.25f, 0.75f, MovementWaterFieldTests.Space, null), Vector3.Zero);
        var left = Point(63.75f);
        var right = field.SampleCentreWater(new(new(64.25f, 0.9f, 0.5f), 0.25f, 0.75f, MovementWaterFieldTests.Space, null), Vector3.Zero);
        Assert.NotEqual(left.Domain, right.Domain);
        Assert.Equal(0, left.Interval!.Value.NominalSurfaceY);
        Assert.Equal(1, right.Interval!.Value.NominalSurfaceY);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var result = field.TraceWater(new(new(new(64, 0.1f, 0.5f), 0.25f, 0.75f, MovementWaterFieldTests.Space, null), Vector3.Zero), Vector3.Zero, MovementWaterFieldTests.Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Contains(contacts.Take(result.ContactsWritten), c => c.Domain == left.Domain && c.Interval.LowerY == -2 && c.Interval.NominalSurfaceY == 0);
        Assert.Contains(contacts.Take(result.ContactsWritten), c => c.Domain == right.Domain && c.Interval.LowerY == 0 && c.Interval.NominalSurfaceY == 1);
    }
}
