using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticSlopedMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

// Continuous analytic producer proofs, limited to one active sloped plane inside a finite enclosure.
// A coverage result is not permission to commit a path that penetrates the matching solid.
public class MovementEnvironmentSlopeTests
{
    const float Radius = 0.25f;
    const float HalfHeight = 0.75f;
    const float Slope = 0.5f;
    const float Skin = 0.001f;

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void SlopedFloorAndCeilingTransitionsReverseAtTheAnalyticCapsuleBoundary(bool ceiling, bool reverse)
    {
        using var scene = new AnalyticSlopedMovementEnvironment(ceiling);
        float sign = ceiling ? -1f : 1f;
        float start = reverse ? 1.25f : 0.75f;
        float end = reverse ? 0.75f : 1.25f;
        MovementBodyQuery body = Body(new Vector3(0f, sign * start, 0f));
        Capture result = Read(scene, new(body, Vector3.UnitY * (sign * (end - start))));
        AssertKnown(result);
        Assert.True(result.Point.InWater);
        // Independent tangent-plane height for an upright capsule, in metres along Y.
        double tangentHeight = 0.5d + (Radius + (double)Skin) * Math.Sqrt(1.25d);
        float transition = (float)((tangentHeight - start) / (end - start));
        float[] cuts = result.Spans.SelectMany(span => new[] { span.EnterFraction, span.ExitFraction }).Distinct().ToArray();
        Assert.Equal(3, cuts.Length);
        Assert.Equal(0f, cuts[0]);
        Assert.InRange(cuts[1], transition - Error, transition + Error);
        Assert.Equal(1f, cuts[2]);
        foreach (MovementCoverageSpan span in result.Spans)
        {
            Assert.Equal(1, span.ContactCount);
            if (span.EnterFraction == span.ExitFraction) continue;
            float middle = (span.EnterFraction + span.ExitFraction) * 0.5f;
            Assert.Equal(reverse ? middle > transition : middle < transition, span.HasDryCoverage);
        }
        AssertColumns(result, ceiling, 0f);

        // The mixed-water pose really overlaps the matching solid. Coverage never licenses that move.
        var contacts = new CapsuleContact[16];
        CapsuleContactResult solid = scene.Physics.QueryCapsuleContacts(new CapsuleShape(Radius, 1f),
            Pose.At(new Vector3(0f, sign * 0.75f, 0f)), 0.001f, contacts);
        Assert.True(solid.Complete);
        Assert.Contains(contacts.Take(solid.Written), contact => Vector3.Dot(contact.Normal, scene.WetNormal) > 0.999f && contact.Separation < 0f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FollowingTheSlopeChangesActualColumnBoundsContinuouslyInBothDirections(bool ceiling)
    {
        foreach (float direction in new[] { -1f, 1f })
        {
            using var scene = new AnalyticSlopedMovementEnvironment(ceiling);
            float sign = ceiling ? -1f : 1f;
            float x = -direction;
            MovementBodyQuery body = Body(new Vector3(x, Slope * x + sign * 1.25f, 0f));
            Capture result = Read(scene, new(body, new Vector3(2f * direction, direction, 0f)));
            AssertKnown(result);
            Assert.True(result.Point.InWater);
            Assert.All(result.Spans, span => { Assert.False(span.HasDryCoverage); Assert.Equal(1, span.ContactCount); });
            AssertColumns(result, ceiling, 0f);
            MovementDomainContact first = result.Contacts[0], last = result.Contacts[^1];
            Assert.InRange(last.IntervalColumnXZ.X - first.IntervalColumnXZ.X, 2f * direction - Error, 2f * direction + Error);
            float difference = ceiling ? last.Interval.UpperY - first.Interval.UpperY : last.Interval.LowerY - first.Interval.LowerY;
            Assert.InRange(difference, direction - Error, direction + Error);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoundedCapsuleCanBeFullyWetWhenItsBoundingPrismCrossesTheSlope(bool ceiling)
    {
        float height = ceiling ? 0.83f : -0.83f;
        using var scene = new AnalyticSlopedMovementEnvironment(ceiling, height: height);
        Capture result = Read(scene, new(Body(Vector3.Zero), Vector3.Zero));
        AssertKnown(result);
        Assert.True(result.Point.InWater);
        Assert.True(HalfHeight + Skin + Slope * (Radius + Skin) > MathF.Abs(height));
        Assert.True(0.5d + (Radius + (double)Skin) * Math.Sqrt(1.25d) < MathF.Abs(height));
        Assert.All(result.Spans, span => { Assert.False(span.HasDryCoverage); Assert.Equal(1, span.ContactCount); });
        AssertColumns(result, ceiling, height);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StationarySkinContactUsesTheDeclaredSlopeAndErrorBudget(bool ceiling)
    {
        using var scene = new AnalyticSlopedMovementEnvironment(ceiling);
        float sign = ceiling ? -1f : 1f;
        float tangentHeight = (float)(0.5d + (Radius + (double)Skin) * Math.Sqrt(1.25d));
        Vector3 centre = Vector3.UnitY * (sign * tangentHeight);
        Capture result = Read(scene, new(Body(centre), Vector3.Zero));
        AssertKnown(result);
        Assert.All(result.Spans, span => Assert.Equal(1, span.ContactCount));
        AssertColumns(result, ceiling, 0f);
        var contacts = new CapsuleContact[16];
        CapsuleContactResult solid = scene.Physics.QueryCapsuleContacts(new CapsuleShape(Radius, 1f), Pose.At(centre), 0.002f, contacts);
        Assert.True(solid.Complete);
        Assert.Contains(contacts.Take(solid.Written), contact => Vector3.Dot(contact.Normal, scene.WetNormal) > 0.999f &&
            MathF.Abs(contact.Separation - Skin) <= solid.CertifiedErrorMetres);
    }

    [Fact]
    public void ASecondParticipatingBoundaryRefusesTheEntireTrace()
    {
        using var scene = new AnalyticSlopedMovementEnvironment(ceiling: false);
        MovementBodyQuery body = Body(new Vector3(0f, 5.7f, 0f));
        Assert.True(scene.Lease.SampleCentreWater(body).InWater);
        AssertRefusedAtomic(scene, new(body, Vector3.Zero));
    }

    [Fact]
    public void MissingSlopeDependencyCannotPublishPartialCoverage()
    {
        using var scene = new AnalyticSlopedMovementEnvironment(ceiling: true);
        scene.MissingDependency = true;
        MovementBodyQuery body = Body(new Vector3(0f, -1f, 0f));
        Assert.Equal(MovementAvailability.Unresolved, scene.Lease.SampleCentreWater(body).Availability);
        AssertRefusedAtomic(scene, new(body, Vector3.UnitX));
    }

    sealed record Capture(MovementWaterPoint Point, MovementCoverageResult Result,
        MovementCoverageSpan[] Spans, MovementDomainContact[] Contacts);

    static Capture Read(AnalyticSlopedMovementEnvironment scene, MovementMediumSweepQuery query)
    {
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        MovementWaterPoint point = scene.Lease.SampleCentreWater(query.Body);
        MovementCoverageResult result = scene.Lease.TraceWater(query, spans, contacts);
        return new(point, result, spans[..result.SpansWritten], contacts[..result.ContactsWritten]);
    }

    static void AssertKnown(Capture result)
    {
        Assert.Equal(MovementAvailability.Known, result.Result.Availability);
        Assert.Equal(Identity, result.Result.Identity);
        Assert.InRange(result.Result.CertifiedErrorMetres, 0f, Error);
        float through = 0f;
        foreach (MovementCoverageSpan span in result.Spans)
        {
            Assert.True(span.IsValid);
            Assert.Equal(through, span.EnterFraction);
            through = span.ExitFraction;
            Assert.InRange(span.ContactStart + span.ContactCount, 0, result.Contacts.Length);
        }
        Assert.Equal(1f, through);
    }

    static void AssertColumns(Capture result, bool ceiling, float height)
    {
        Vector3 expectedNormal = Vector3.Normalize(new Vector3(-Slope, 1f, 0f)) * (ceiling ? 1f : -1f);
        foreach (MovementDomainContact contact in result.Contacts)
        {
            Assert.True(contact.IsValid);
            Assert.Equal(Room, contact.Space);
            Assert.Equal(Water, contact.Domain);
            Assert.InRange(Vector3.Distance(expectedNormal, contact.Normal), 0f, Error);
            float plane = Slope * contact.IntervalColumnXZ.X + height;
            Assert.Equal(plane, ceiling ? contact.Interval.UpperY : contact.Interval.LowerY);
            Assert.Equal(!ceiling, contact.Interval.UpperIsFreeSurface);
            Assert.Equal(6f, contact.Interval.NominalSurfaceY);
        }
    }

    static void AssertRefusedAtomic(AnalyticSlopedMovementEnvironment scene, MovementMediumSweepQuery query)
    {
        MovementCoverageSpan span = new(0.5f, 1f, 0, 1, true);
        MovementDomainContact contact = new(Water, Room, new(-4f, 2f, 2f, true, "sentinel-low", "sentinel-high"),
            Vector2.Zero, Vector3.UnitY, 0.75f, "sentinel", 123u);
        MovementCoverageSpan[] spans = [span];
        MovementDomainContact[] contacts = [contact];
        MovementCoverageResult result = scene.Lease.TraceWater(query, spans, contacts);
        Assert.Equal(MovementAvailability.Unresolved, result.Availability);
        Assert.Equal(span, spans[0]);
        Assert.Equal(contact, contacts[0]);
    }

    static MovementBodyQuery Body(Vector3 centre) => new(centre, Radius, HalfHeight, Room, null);
}
