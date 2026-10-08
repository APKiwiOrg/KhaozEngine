using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

// The two wet arms occupy x<0 or z<0 within a finite room. The positive-X/positive-Z notch is dry.
public class MovementEnvironmentNonrectangularTests
{
    const float Radius = 0.25f;
    const float Skin = 0.001f;

    [Fact]
    public void TheUnionBoundingBoxCannotFillItsDryNotch()
    {
        using var scene = Scene();
        Capture result = Trace(scene, Body(0.5f, 0.5f), Vector3.Zero);
        AssertKnown(result);
        Assert.False(result.Point.InWater);
        Assert.Empty(result.Contacts);
        Assert.All(result.Spans, span => Assert.True(span.HasDryCoverage));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DryCentreAndBothWetArmsRemainMixedInEitherDeclarationOrder(bool reverse)
    {
        using var scene = Scene(reverse);
        Capture result = Trace(scene, Body(0.1f, 0.1f), Vector3.Zero);
        AssertKnown(result);
        Assert.False(result.Point.InWater);
        Assert.All(result.Spans, span =>
        {
            Assert.True(span.HasDryCoverage);
            Assert.Equal(2, span.ContactCount);
            MovementDomainContact[] contacts = result.Contacts.Skip(span.ContactStart).Take(span.ContactCount).ToArray();
            Assert.All(contacts, contact => Assert.Equal(Domain("water"), contact.Domain));
            Assert.Equal(2, contacts.Select(contact => contact.CoverageRegionHandle).Distinct().Count());
        });
    }

    [Fact]
    public void BothArmsCanCoverTheCapsuleWhenNeitherArmAloneContainsIt()
    {
        using var scene = Scene();
        Capture result = Trace(scene, Body(-0.2f, -0.2f), Vector3.Zero);
        AssertKnown(result);
        Assert.True(result.Point.InWater);
        // The capsule crosses both X=0 and Z=0, but cannot reach the missing quadrant's corner.
        Assert.True(Radius + Skin > 0.2f);
        Assert.True(Math.Sqrt(2d * 0.2d * 0.2d) > Radius + Skin);
        Assert.All(result.Spans, span => { Assert.False(span.HasDryCoverage); Assert.Equal(2, span.ContactCount); });
    }

    [Fact]
    public void ANotchCrossingChangesDryCoverageAtTheActualCapsuleBoundary()
    {
        using var scene = Scene();
        Capture result = Trace(scene, Body(0.5f, 0.1f), -Vector3.UnitX);
        AssertKnown(result);
        Assert.False(result.Point.InWater);
        // At fixed positive Z, the capsule stops reaching the dry quadrant when centre X=-radius-skin.
        float lastDry = 0.5f + Radius + Skin;
        Assert.Contains(result.Spans, span => MathF.Abs(span.ExitFraction - lastDry) <= 0.00001f);
        foreach (MovementCoverageSpan span in result.Spans)
        {
            Assert.True(span.ContactCount > 0);
            if (span.ExitFraction == span.EnterFraction) continue;
            float middle = (span.EnterFraction + span.ExitFraction) * 0.5f;
            Assert.Equal(middle < lastDry, span.HasDryCoverage);
        }
    }

    [Fact]
    public void TwoClosedStartTangenciesRetainBothOutwardBoundaryConstraints()
    {
        using var scene = Scene();
        float extent = Radius + Skin;
        Capture result = Trace(scene, Body(extent, extent), Vector3.Zero);
        AssertKnown(result);
        Assert.False(result.Point.InWater);
        Assert.All(result.Spans, span =>
        {
            Assert.True(span.HasDryCoverage);
            Assert.Equal(2, span.ContactCount);
            Vector3[] normals = result.Contacts.Skip(span.ContactStart).Take(span.ContactCount).Select(c => c.Normal).ToArray();
            Assert.Contains(Vector3.UnitX, normals);
            Assert.Contains(Vector3.UnitZ, normals);
        });
    }

    [Fact]
    public void MissingNeighborClassificationCannotTurnTheUnionIntoKnownDry()
    {
        using var scene = Scene();
        scene.MissingContainment = true;
        using var lease = scene.Acquire("room");
        MovementCoverageSpan span = new(0.5f, 1f, 0, 1, true);
        MovementDomainContact contact = new(Domain("sentinel"), Space("room"), new(-4f, 0f, 0f, true, "low", "upper"),
            Vector2.Zero, Vector3.UnitY, 0.75f, "sentinel", 123u, MovementContactOverlap.Overlapping);
        MovementCoverageSpan[] spans = [span];
        MovementDomainContact[] contacts = [contact];
        MovementCoverageResult result = lease.TraceWater(new(Body(0.1f, 0.1f), Vector3.Zero), spans, contacts);
        Assert.Equal(MovementAvailability.Unresolved, result.Availability);
        Assert.Equal(span, spans[0]);
        Assert.Equal(contact, contacts[0]);
    }

    sealed record Capture(MovementWaterPoint Point, MovementCoverageResult Result,
        MovementCoverageSpan[] Spans, MovementDomainContact[] Contacts);

    static Capture Trace(AnalyticMovementEnvironment scene, MovementBodyQuery body, Vector3 delta)
    {
        using var lease = scene.Acquire("room");
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        MovementWaterPoint point = lease.SampleCentreWater(body);
        MovementCoverageResult result = lease.TraceWater(new(body, delta), spans, contacts);
        return new(point, result, spans[..result.SpansWritten], contacts[..result.ContactsWritten]);
    }

    static void AssertKnown(Capture capture)
    {
        Assert.Equal(MovementAvailability.Known, capture.Point.Availability);
        Assert.Equal(MovementAvailability.Known, capture.Result.Availability);
        Assert.Equal(Identity, capture.Result.Identity);
        Assert.InRange(capture.Result.CertifiedErrorMetres, 0f, Skin);
        float next = 0f;
        foreach (MovementCoverageSpan span in capture.Spans)
        {
            Assert.True(span.IsValid);
            Assert.Equal(next, span.EnterFraction);
            next = span.ExitFraction;
            Assert.InRange(span.ContactStart + span.ContactCount, 0, capture.Contacts.Length);
        }
        Assert.Equal(1f, next);
        Assert.All(capture.Contacts, contact => Assert.True(contact.IsValid));
    }

    static MovementBodyQuery Body(float x, float z) => new(new Vector3(x, -1f, z), Radius, 0.75f, Space("room"), null);
    static AnalyticMovementEnvironment Scene(bool reverse = false)
    {
        var room = new Room("room", new AnalyticBox(new Vector3(-8f), new Vector3(8f)));
        Water a = new("water", "room", new AnalyticBox(new Vector3(-4f, -4f, -4f), new Vector3(0f, 0f, 4f)), 0f);
        Water b = new("water", "room", new AnalyticBox(new Vector3(0f, -4f, -4f), new Vector3(4f, 0f, 0f)), 0f);
        return new AnalyticMovementEnvironment([room], reverse ? [b, a] : [a, b]);
    }
}
