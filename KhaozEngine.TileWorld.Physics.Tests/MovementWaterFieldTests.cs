using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Tests.TileWorld.Physics.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.Physics;

public class MovementWaterFieldTests
{
    internal static readonly MovementQueryIdentity Identity = new("tile-test", 1, "snapshot");
    internal static readonly MovementSpaceKey Space = new("water-world", "ordinary");

    internal static TileWorldDocument WaterWorld()
    {
        var doc = TileWorldPhysicsTestData.FlatWorld(new RegionCoord(0, -1), new RegionCoord(0, 0));
        doc.SetUnderlay(0, -1, 0, TileWorldPhysicsTestData.Water);
        // Keep the measured world SW/NE corners and minimum-Z tangent edge deep. The opposite rim
        // supplies the level while the box-oracle paths remain clear of the actual drawn bed.
        doc.SetCornerHeightCm(0, -1, 0, 2);
        doc.SetCornerHeightCm(1, -1, 0, -200);
        doc.SetCornerHeightCm(0, 0, 0, -200);
        doc.SetCornerHeightCm(1, 0, 0, -200);
        doc.SetCornerHeightCm(0, 1, 0, -200);
        doc.SetCornerHeightCm(1, 1, 0, -200);
        return doc;
    }

    internal static TileMediumSampler Field(TileWorldDocument doc) =>
        TileWorldColliders.Build(doc, TileWorldPhysicsTestData.Catalogs(), "water-world").Medium;

    internal static TileMediumSampler SingleWaterTile(int tileX, int tileZ, float tileSize)
    {
        var region = RegionCoord.Of(tileX, tileZ);
        var doc = TileWorldPhysicsTestData.FlatWorld(region);
        doc.TileSize = tileSize;
        // Rounded world axes can select a neighboring lattice cell. Its captured bed must agree
        // with the -4 m analytic ground used by the ordinary-frame fixture too.
        for (int z = region.OriginZ; z < region.OriginZ + TileRegion.Size; z++)
            for (int x = region.OriginX; x < region.OriginX + TileRegion.Size; x++)
                doc.SetCornerHeightCm(x, z, 0, -400);
        doc.SetUnderlay(tileX, tileZ, 0, TileWorldPhysicsTestData.Water);
        doc.SetCornerHeightCm(tileX, tileZ, 0, -400);
        doc.SetCornerHeightCm(tileX + 1, tileZ, 0, 2);
        return new(doc, TileWorldPhysicsTestData.Catalogs(), new[] { region }, "water-world");
    }

    [Theory]
    [InlineData(33554434, 0, 0.125f, 4194304f, 0f, 0f, -0.0625f, true)]
    [InlineData(33554434, 0, 0.125f, 4194304f, 0f, -0.0001f, -0.0625f, false)]
    [InlineData(33554434, 0, 0.125f, 4194304f, 0f, 0.5f, -0.0625f, false)]
    [InlineData(33554434, 0, 0.125f, 4194304f, 0f, 0.5001f, -0.0625f, false)]
    [InlineData(33554434, 0, 0.125f, 4194304f, 0f, 0.25f, -0.1251f, false)]
    [InlineData(33554434, 0, 0.125f, 4194304f, 0f, 0.25f, 0.0001f, false)]
    [InlineData(-33554438, 0, 0.125f, -4194305f, 0f, 0f, -0.0625f, true)]
    [InlineData(0, 33554434, 0.125f, 0f, -4194304f, 0.0625f, 0f, true)]
    [InlineData(2, 0, 1f, 0f, 0f, 2f, -0.5f, true)]
    [InlineData(2, 0, 1f, 0f, 0f, 1.9999f, -0.5f, false)]
    [InlineData(2, 0, 1f, 0f, 0f, 3f, -0.5f, false)]
    public void RoundedColumnFacesDetermineMembershipAtLargeAndSmallAddresses(int tileX, int tileZ,
        float tileSize, float originX, float originZ, float localX, float localZ, bool wet)
    {
        var field = SingleWaterTile(tileX, tileZ, tileSize);
        var point = field.SampleCentreWater(new(new(localX, -1, localZ), 0.03125f, 0.75f, Space, null),
            new(originX, 0, originZ));
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.Equal(wet, point.InWater);
        if (wet)
        {
            Assert.Equal(-4f, point.Interval!.Value.LowerY);
            Assert.Equal(0f, point.Interval.Value.UpperY);
            Assert.NotNull(point.Domain);
        }
        else
        {
            Assert.Null(point.Interval);
            Assert.Null(point.Domain);
        }
    }

    [Fact]
    public void RoundedMinimumXFacePointAgreesWithTraceCoverage()
    {
        var field = SingleWaterTile(33554434, 0, 0.125f);
        var body = new MovementBodyQuery(new(0, -1, -0.0625f), 0.03125f, 0.75f, Space, null);
        Vector3 origin = new(4194304, 0, 0);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var trace = field.TraceWater(new(body, Vector3.Zero), origin, Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, trace.Availability);
        Assert.True(trace.ContactsWritten > 0);
        Assert.All(contacts.Take(trace.ContactsWritten), contact =>
            Assert.Equal(MovementContactOverlap.Overlapping, contact.SpanOverlap));
        var point = field.SampleCentreWater(body, origin);
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.True(point.InWater);
        Assert.All(contacts.Take(trace.ContactsWritten), contact => Assert.Equal(point.Domain, contact.Domain));
    }

    [Fact]
    public void FullyContainedSweepCertifiesWithZeroAndTinyNonzeroComponents()
    {
        var field = Field(WaterWorld());
        var body = new MovementBodyQuery(new(0.5f, -0.5f, 0.5f), 0.1f, 0.25f, Space, null);
        foreach (float dx in new[] { 0f, 1e-15f })
        {
            var spans = new MovementCoverageSpan[64];
            var contacts = new MovementDomainContact[256];
            var result = field.TraceWater(new(body, new(dx, 0, 0.1f)), Vector3.Zero, Identity, spans, contacts);
            Assert.Equal(MovementAvailability.Known, result.Availability);
            Assert.True(result.IsValid);
            Assert.Equal(Identity, result.Identity);
            Assert.InRange(result.CertifiedErrorMetres, 0f, 0.001f);
            var span = Assert.Single(spans.Take(result.SpansWritten));
            Assert.True(span.IsValid);
            Assert.Equal(0, span.EnterFraction);
            Assert.Equal(1, span.ExitFraction);
            Assert.False(span.HasDryCoverage);
            Assert.True(span.ContactCount > 0);
            Assert.Equal(result.ContactsWritten, span.ContactCount);
            Assert.All(contacts.Take(result.ContactsWritten), contact =>
            {
                Assert.True(contact.IsValid);
                Assert.Equal(Space, contact.Space);
                Assert.Equal(MovementContactOverlap.Overlapping, contact.SpanOverlap);
            });
        }
    }

    [Fact]
    public void RoundedCornerDoesNotUseAnExpandedBox()
    {
        var field = Field(WaterWorld());
        var axis = new Vector3(-0.09f, -0.5f, -0.09f);
        const float radius = 0.1f;
        var oracle = new TileWaterCoverageOracle(new(0, -2, 0), new(1, 0, 1));
        Assert.InRange(Math.Sqrt(0.09d * 0.09d + 0.09d * 0.09d), 0.1272d, 0.1274d);
        Assert.False(oracle.Overlaps(axis, radius, 0.25f));
        var spans = new MovementCoverageSpan[64];
        var trace = field.TraceWater(new(new(axis, radius, 0.25f, Space, null), Vector3.Zero), Vector3.Zero, Identity, spans, new MovementDomainContact[256]);
        Assert.Equal(MovementAvailability.Known, trace.Availability);
        Assert.Equal(0, trace.ContactsWritten);
        Assert.True(spans[0].HasDryCoverage);
    }

    [Theory]
    [InlineData(-1, -0.5f, 0.5f, 3, 0, 0)] // face
    [InlineData(-1, -0.5f, -0.05f, 3, 0, 0)] // edge
    [InlineData(-1, -0.5f, -1, 3, 0, 3)] // diagonal corner
    [InlineData(0.5f, 1, 0.5f, 0, -4, 0)] // vertical caps
    [InlineData(-0.05f, 1, -0.05f, 0, -4, 0)] // rounded vertical corner
    [InlineData(-1, -0.5f, -0.1f, 3, 0, 0)] // tangency along face edge
    public void ExactCoverageAgreesOnFacesEdgesCornersDiagonalTangencyAndVerticalCaps(
        float x, float y, float z, float dx, float dy, float dz)
    {
        var field = Field(WaterWorld());
        var body = new MovementBodyQuery(new(x, y, z), 0.1f, 0.25f, Space, null);
        var delta = new Vector3(dx, dy, dz);
        var oracle = new TileWaterCoverageOracle(new(0, -2, 0), new(1, 0, 1));
        var expected = oracle.Path(body.Centre, delta, body.Radius, body.HalfHeight)!.Value;
        Assert.True(new AnalyticBox(new(0, -2, 0), new(1, 0, 1)).IntersectCapsule(body.Centre, delta, body.Radius, body.HalfHeight, out double crossEnter, out double crossExit));
        Assert.InRange(Math.Abs(expected.Enter - crossEnter), 0, 1e-9);
        Assert.InRange(Math.Abs(expected.Exit - crossExit), 0, 1e-9);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var result = field.TraceWater(new(body, delta), Vector3.Zero, Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(Identity, result.Identity);
        Assert.InRange(result.CertifiedErrorMetres, 0f, 0.001f);
        Assert.Equal(0, spans[0].EnterFraction);
        Assert.Equal(1, spans[result.SpansWritten - 1].ExitFraction);
        for (int i = 1; i < result.SpansWritten; i++) Assert.Equal(spans[i - 1].ExitFraction, spans[i].EnterFraction);
        var wet = spans.Take(result.SpansWritten).Where(s => contacts.AsSpan(s.ContactStart, s.ContactCount).ToArray()
            .Any(c => c.SpanOverlap == MovementContactOverlap.Overlapping)).ToArray();
        double length = delta.Length();
        double certifiedEnter = wet.Length > 0 ? wet[0].EnterFraction : contacts.Take(result.ContactsWritten).Min(c => c.Fraction) - result.CertifiedErrorMetres / length;
        double certifiedExit = wet.Length > 0 ? wet[^1].ExitFraction : contacts.Take(result.ContactsWritten).Max(c => c.Fraction) + result.CertifiedErrorMetres / length;
        Assert.True(certifiedEnter <= expected.Enter);
        Assert.True(certifiedExit >= expected.Exit);
        Assert.InRange((expected.Enter - certifiedEnter) * length, 0, result.CertifiedErrorMetres + 1e-7);
        Assert.InRange((certifiedExit - expected.Exit) * length, 0, result.CertifiedErrorMetres + 1e-7);
        var domain = field.SampleCentreWater(new(new(0.5f, -0.5f, 0.5f), 0.1f, 0.25f, Space, null), Vector3.Zero).Domain;
        Assert.All(contacts.Take(result.ContactsWritten), c =>
        {
            Assert.Equal(domain, c.Domain);
            Assert.InRange(c.IntervalColumnXZ.X, 0, 1);
            Assert.InRange(c.IntervalColumnXZ.Y, 0, 1);
        });
        if (z == -0.1f && dz == 0)
            Assert.All(contacts.Take(result.ContactsWritten), c =>
            {
                Assert.Equal(MovementContactOverlap.Tangent, c.Overlap);
                Assert.Equal(MovementContactOverlap.Tangent, c.SpanOverlap);
            });
    }

    [Fact]
    public void NegativeTilesAndReversedWorldZAgreeWithAuthoredColumns()
    {
        var doc = WaterWorld();
        var field = Field(doc);
        var body = new MovementBodyQuery(TileWorldSpace.ToWorld(0.5f, -0.5f, -0.5f, 1), 0.1f, 0.25f, Space, null);
        var snapshotPoint = field.SampleCentreWater(body, Vector3.Zero);
        Assert.Equal(field.MediumAt(body.Centre.X, body.Centre.Z, body.Feet.Y).InWater, snapshotPoint.InWater);
        Assert.Equal(0, snapshotPoint.Interval!.Value.NominalSurfaceY);
        doc.SetUnderlay(0, -1, 0, 1);
        doc.SetCornerHeightCm(0, -1, 0, 100);
        Assert.Equal(snapshotPoint, field.SampleCentreWater(body, Vector3.Zero));
        Assert.True(field.MediumAt(0.5f, 0.5f, -5).InWater);
        Assert.False(field.SampleCentreWater(new(new(0.5f, -5, 0.5f), 0.1f, 0.25f, Space, null), Vector3.Zero).InWater);
        Assert.False(field.SampleCentreWater(new(new(2.5f, -0.5f, 0.5f), 0.1f, 0.25f, Space, null), Vector3.Zero).InWater);
        Assert.Equal(0, field.Generation);
        Assert.NotEqual(field.SemanticIdentity, Field(doc).SemanticIdentity);
    }

    [Fact]
    public void AdjacentBodiesKeepRegionIdentityAndSuppressOwnInternalFaces()
    {
        var doc = TileWorldPhysicsTestData.FlatWorld(new(0, -1), new(1, -1));
        foreach (int x in new[] { 62, 63, 64 })
        {
            doc.SetUnderlay(x, -1, 0, TileWorldPhysicsTestData.Water);
            doc.SetCornerHeightCm(x, -1, 0, -200);
        }
        // The shared edge stays on the drawn bed. Each body's outer corner supplies its rim.
        doc.SetCornerHeightCm(62, -1, 0, 2);
        doc.SetCornerHeightCm(65, -1, 0, 2);
        var field = Field(doc);
        MovementDomainKey? Domain(float x) => field.SampleCentreWater(new(new(x, -0.5f, 0.5f), 0.1f, 0.25f, Space, null), Vector3.Zero).Domain;
        Assert.Equal(Domain(62.5f), Domain(63.5f));
        Assert.NotEqual(Domain(63.5f), Domain(64.5f));
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var result = field.TraceWater(new(new(new(63, -0.5f, 0.5f), 0.1f, 0.25f, Space, null), Vector3.Zero), Vector3.Zero, Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.False(spans[0].HasDryCoverage);
        Assert.DoesNotContain(contacts.Take(result.ContactsWritten), c => c.BoundaryId.EndsWith("/max-x", StringComparison.Ordinal) || c.BoundaryId.EndsWith("/min-x", StringComparison.Ordinal));
    }

    [Fact]
    public void OneShortBuffersRefuseAtomically()
    {
        var field = Field(WaterWorld());
        var query = new MovementMediumSweepQuery(new(new(-1, -0.5f, 0.5f), 0.1f, 0.25f, Space, null), new(3, 0, 0));
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var good = field.TraceWater(query, Vector3.Zero, Identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, good.Availability);
        var spanSentinels = spans.ToArray();
        var contactSentinels = contacts.ToArray();
        foreach (bool shortSpans in new[] { true, false })
        {
            var result = field.TraceWater(query, Vector3.Zero, Identity,
                spans.AsSpan(0, shortSpans ? good.SpansWritten - 1 : 64),
                contacts.AsSpan(0, shortSpans ? 256 : good.ContactsWritten - 1));
            Assert.Equal(MovementAvailability.CapacityExceeded, result.Availability);
            Assert.Equal(0, result.SpansWritten);
            Assert.Equal(0, result.ContactsWritten);
            Assert.Equal(spanSentinels, spans);
            Assert.Equal(contactSentinels, contacts);
        }
    }
}
