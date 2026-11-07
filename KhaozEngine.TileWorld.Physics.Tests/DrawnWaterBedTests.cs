using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class DrawnWaterBedTests
{
    static readonly MovementSpaceKey Space = new("water-world", "ordinary");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PointMembershipUsesTheRepresentedTriangleAtSeamsEdgesAndVertices(bool levelTriangle)
    {
        var world = World(levelTriangle);
        var colliders = TileWorldColliders.Build(world, TileWorldPhysicsTestData.Catalogs(), Space.WorldId);
        Vector2[] axes = [new(8, -8), new(8, -8.5f), new(MathF.BitIncrement(8), -8.5f),
            new(8.125f, -8.875f), new(8.5f, MathF.BitDecrement(-8.5f)), new(8.5f, -8.5f),
            new(8.5f, MathF.BitIncrement(-8.5f)), new(8.75f, -8.75f), new(9, -9),
            new(MathF.BitDecrement(9), -8.5f), new(MathF.BitIncrement(9), -8.5f)];
        foreach (Vector2 axis in axes)
        {
            float bed = colliders.MovementGroundHeight(axis.X, axis.Y);
            // half = -bed and centre = 0 put exact feet on this represented boundary. Adjacent centre
            // floats bracket it even when their tiny addends cannot survive a binary64 sum with the bed.
            foreach (float centre in new[] { MathF.BitDecrement(0), 0, MathF.BitIncrement(0) })
            {
                var point = colliders.Medium.SampleCentreWater(new(new(axis.X, centre, axis.Y), 0.01f, -bed, Space, null),
                    Vector3.Zero);
                Assert.Equal(MovementAvailability.Known, point.Availability);
                Assert.Equal(centre >= 0, point.InWater);
                if (point.InWater) Assert.Equal(bed, point.Interval!.Value.LowerY);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapsuleWhollyBelowTheDrawnBedHasNoWetTraceCoverage(bool levelTriangle)
    {
        var colliders = TileWorldColliders.Build(World(levelTriangle), TileWorldPhysicsTestData.Catalogs(), Space.WorldId);
        float bed = colliders.MovementGroundHeight(8.75f, -8.75f);
        var body = new MovementBodyQuery(new(8.75f, bed - 0.04f, -8.75f), 0.005f, 0.005f, Space, null);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var trace = colliders.Medium.TraceWater(new(body, new(0.01f, 0, 0.01f)), Vector3.Zero,
            MovementWaterFieldTests.Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, trace.Availability);
        Assert.Equal(0, trace.ContactsWritten);
        Assert.True(Assert.Single(spans.Take(trace.SpansWritten)).HasDryCoverage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TraceIntervalsUseTheSameLocatedTriangleAsSupport(bool levelTriangle)
    {
        var colliders = TileWorldColliders.Build(World(levelTriangle), TileWorldPhysicsTestData.Catalogs(), Space.WorldId);
        float bed = colliders.MovementGroundHeight(8.5f, -8.5f);
        var body = new MovementBodyQuery(new(8.5f, bed + 0.04f, -8.5f), 0.005f, 0.005f, Space, null);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var trace = colliders.Medium.TraceWater(new(body, new(0.01f, 0, 0.01f)), Vector3.Zero,
            MovementWaterFieldTests.Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, trace.Availability);
        Assert.True(trace.ContactsWritten > 0);
        Assert.All(spans.Take(trace.SpansWritten), span => Assert.False(span.HasDryCoverage));
        Assert.All(contacts.Take(trace.ContactsWritten), contact =>
        {
            Assert.Equal(colliders.MovementGroundHeight(contact.IntervalColumnXZ.X, contact.IntervalColumnXZ.Y),
                contact.Interval.LowerY);
            Assert.Equal(MovementContactOverlap.Overlapping, contact.SpanOverlap);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheOuterBedEdgeRetainsExactHalfOpenMembership(bool levelTriangle)
    {
        var field = TileWorldColliders.Build(World(levelTriangle), TileWorldPhysicsTestData.Catalogs(), Space.WorldId).Medium;
        foreach (float x in new[] { MathF.BitDecrement(8), 8, MathF.BitIncrement(8) })
        {
            var point = field.SampleCentreWater(new(new(x, 0, -8.5f), 0.01f, 0.5f, Space, null), Vector3.Zero);
            Assert.Equal(MovementAvailability.Known, point.Availability);
            Assert.Equal(x >= 8, point.InWater);
        }
    }

    [Fact]
    public void WaterAndSupportRetainOneCapturedBedAfterAnEdit()
    {
        var document = World(false);
        var colliders = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), Space.WorldId);
        float bed = colliders.MovementGroundHeight(8.5f, -8.5f);
        document.SetCornerHeightCm(8, 8, 0, -20);
        var point = colliders.Medium.SampleCentreWater(new(new(8.5f, 0, -8.5f), 0.01f, -bed, Space, null), Vector3.Zero);
        Assert.True(point.InWater);
        Assert.Equal(bed, colliders.MovementGroundHeight(8.5f, -8.5f));
        Assert.Equal(bed, point.Interval!.Value.LowerY);
    }

    [Fact]
    public void TraceAccountsForRepresentedBedRoundingAtASubUlpRebase()
    {
        var colliders = TileWorldColliders.Build(World(false), TileWorldPhysicsTestData.Catalogs(), Space.WorldId);
        float bed = colliders.MovementGroundHeight(8.5f, -8.5f);
        var body = new MovementBodyQuery(new(8.5f, -0.7f, -8.5f), 5e-10f, 5e-10f, Space, null);
        var origin = new Vector3(0, 3e-8f, 0);
        double top = (double)body.Centre.Y + origin.Y + body.HalfHeight;
        Assert.True(top < bed);
        Assert.False(colliders.Medium.SampleCentreWater(body, origin).InWater);
        var trace = colliders.Medium.TraceWater(new(body, Vector3.Zero), origin, MovementWaterFieldTests.Identity,
            new MovementCoverageSpan[64], new MovementDomainContact[256]);
        Assert.Equal(MovementAvailability.Known, trace.Availability);
        Assert.True(trace.ContactsWritten == 0 || trace.CertifiedErrorMetres >= bed - top,
            $"An overlapping trace must account for the {bed - top:R} m represented bed difference.");
    }

    [Fact]
    public void UniformCapturedBedKeepsExactCoverageAtCoarseAbsoluteCoordinates()
    {
        const int x = 33554434;
        var region = RegionCoord.Of(x, 0);
        var document = TileWorldPhysicsTestData.FlatWorld(region);
        document.TileSize = 0.125f;
        for (int z = 0; z < 64; z++)
            for (int column = region.OriginX; column < region.OriginX + 64; column++)
                document.SetCornerHeightCm(column, z, 0, -400);
        document.SetUnderlay(x, 0, 0, TileWorldPhysicsTestData.Water);
        document.SetCornerHeightCm(x + 1, 0, 0, 2);
        var colliders = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), Space.WorldId);
        var body = new MovementBodyQuery(new(0, -1, -0.0625f), 0.03125f, 0.75f, Space, null);
        var origin = new Vector3(4194304, 0, 0);
        var point = colliders.Medium.SampleCentreWater(body, origin);
        Assert.True(point.InWater);
        Assert.Equal(-4, colliders.MovementGroundHeight(body.Centre.X + origin.X, body.Centre.Z));
        var contacts = new MovementDomainContact[256];
        var trace = colliders.Medium.TraceWater(new(body, Vector3.Zero), origin, MovementWaterFieldTests.Identity,
            new MovementCoverageSpan[64], contacts);
        Assert.Equal(MovementAvailability.Known, trace.Availability);
        Assert.True(trace.ContactsWritten > 0);
        Assert.All(contacts.Take(trace.ContactsWritten), contact => Assert.Equal(-4, contact.Interval.LowerY));
    }

    [Fact]
    public void AFollowingWetCrossingDoesNotMakeTheTangentOnlyPrefixPositive()
    {
        var field = TileWorldColliders.Build(World(false), TileWorldPhysicsTestData.Catalogs(), Space.WorldId).Medium;
        var body = new MovementBodyQuery(new(7.5f, -0.3f, -8.5f), 0.125f, 0.125f, Space, null);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var trace = field.TraceWater(new(body, Vector3.UnitX), Vector3.Zero, MovementWaterFieldTests.Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, trace.Availability);
        var prefix = Assert.Single(spans.Take(trace.SpansWritten), span => span.EnterFraction == 0 && span.ExitFraction == 0.375f);
        Assert.True(prefix.HasDryCoverage);
        Assert.True(prefix.ContactCount > 0);
        Assert.All(contacts.AsSpan(prefix.ContactStart, prefix.ContactCount).ToArray(), contact =>
        {
            Assert.Equal(MovementContactOverlap.Tangent, contact.Overlap);
            Assert.Equal(MovementContactOverlap.Tangent, contact.SpanOverlap);
        });
    }

    [Fact]
    public void ALevelTriangleAtTheFreeSurfaceHasNoWaterVolume()
    {
        var colliders = TileWorldColliders.Build(World(false), TileWorldPhysicsTestData.Catalogs(), Space.WorldId);
        Assert.Equal(0, colliders.MovementGroundHeight(9.75f, -9.75f));
        var body = new MovementBodyQuery(new(9.75f, 0, -9.75f), 0.005f, 0.005f, Space, null);
        var trace = colliders.Medium.TraceWater(new(body, Vector3.Zero), Vector3.Zero, MovementWaterFieldTests.Identity,
            new MovementCoverageSpan[64], new MovementDomainContact[256]);
        Assert.Equal(MovementAvailability.Known, trace.Availability);
        Assert.Equal(0, trace.ContactsWritten);
    }

    [Fact]
    public void TheOnlyRepresentableCoordinateInALoadedRectangleStillSamplesItsGround()
    {
        var region = RegionCoord.Of(int.MinValue, -1);
        var document = TileWorldPhysicsTestData.FlatWorld(region);
        document.SetCornerHeightCm(int.MinValue, -1, 0, -200);
        var ground = new TileGroundSampler(document, [region]);
        Assert.Equal(-2, ground.HeightAt(int.MinValue, 0.5f));
    }

    [Fact]
    public void AFlatPatchKeepsItsCapturedBedBesideAFarSlopedRim()
    {
        var document = TileWorldPhysicsTestData.FlatWorld();
        document.TileSize = 0.05f;
        for (int z = 0; z < 64; z++)
            for (int x = 0; x < 64; x++)
            {
                document.SetCornerHeightCm(x, z, 0, -400);
                if (x == 8) document.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
            }
        document.SetCornerHeightCm(8, 0, 0, 2);
        var field = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), Space.WorldId).Medium;
        foreach (float radius in new[] { 0.25f, MathF.BitIncrement(0.25f) })
        {
            var body = new MovementBodyQuery(new(0, -3.25f, -1), radius, 0.75f, Space, null);
            var contacts = new MovementDomainContact[256];
            var trace = field.TraceWater(new(body, new(1.2f, 0, 0)), Vector3.Zero, MovementWaterFieldTests.Identity,
                new MovementCoverageSpan[64], contacts);
            Assert.Equal(MovementAvailability.Known, trace.Availability);
            Assert.True(trace.ContactsWritten > 0);
            Assert.All(contacts.Take(trace.ContactsWritten), contact => Assert.Equal(-4, contact.Interval.LowerY));
        }
    }

    [Fact]
    public void TheSharedBedCannotUndershootItsDrawnVertexRangeAtASeam()
    {
        var document = TileWorldPhysicsTestData.FlatWorld();
        document.SetUnderlay(0, 0, 0, TileWorldPhysicsTestData.Water);
        document.SetCornerHeightCm(0, 0, 0, 2);
        var colliders = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), Space.WorldId);
        const float x = 0.8f, z = -0.19999998807907104f;
        Assert.Equal(0, colliders.Ground.HeightAt(x, z));
        Assert.Equal(0, colliders.MovementGroundHeight(x, z));
        var body = new MovementBodyQuery(new(x, 0, z), 1e-10f, 1e-10f, Space, null);
        Assert.False(colliders.Medium.SampleCentreWater(body, Vector3.Zero).InWater);
    }

    [Fact]
    public void AnOwnedColumnWithNoReachableCapturedBedIsUnresolved()
    {
        const int tileX = 33554434;
        var document = new TileWorldDocument { TileSize = 0.125f };
        document.GetOrCreateRegion(RegionCoord.Of(tileX, 0));
        document.SetUnderlay(tileX, 0, 0, TileWorldPhysicsTestData.Water);
        document.SetCornerHeightCm(tileX, 0, 0, -400);
        document.SetCornerHeightCm(tileX + 1, 0, 0, 2);
        var field = TileWorldColliders.Build(document, TileWorldPhysicsTestData.Catalogs(), Space.WorldId).Medium;
        var point = field.SampleCentreWater(new(new(0, -1, -0.0625f), 0.03125f, 0.75f, Space, null), new(4194304, 0, 0));
        Assert.Equal(MovementAvailability.Unresolved, point.Availability);
    }

    static TileWorldDocument World(bool levelTriangle)
    {
        var document = TileWorldPhysicsTestData.FlatWorld();
        for (int z = 8; z <= 9; z++)
            for (int x = 8; x <= 9; x++) document.SetUnderlay(x, z, 0, TileWorldPhysicsTestData.Water);
        document.SetCornerHeightCm(8, 8, 0, -80);
        document.SetCornerHeightCm(9, 8, 0, (short)(levelTriangle ? -40 : -60));
        document.SetCornerHeightCm(8, 9, 0, (short)(levelTriangle ? -40 : -80));
        document.SetCornerHeightCm(9, 9, 0, (short)(levelTriangle ? -40 : -60));
        document.SetCornerHeightCm(10, 8, 0, 2);
        return document;
    }
}
