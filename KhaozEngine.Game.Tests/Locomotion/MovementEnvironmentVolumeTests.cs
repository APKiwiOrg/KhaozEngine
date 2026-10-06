using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Locomotion.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Locomotion.Fixtures.AnalyticMovementEnvironment;

namespace KhaozEngine.Tests.Locomotion;

// Finite producer + lease consumer proofs. No native sampler, native seam or movement acceptance claim.
public class MovementEnvironmentVolumeTests
{
    static readonly AnalyticBox RoomBounds = Box(-8f, -8f, 8f, 8f);
    const float Radius = 0.25f;
    const float HalfHeight = 0.75f;
    const float Skin = 0.001f;

    [Fact]
    public void DryCaveBelowOceanDoesNotAcquireItsProjectedWater()
    {
        using var scene = new AnalyticMovementEnvironment(
            [new Room("cave", Box(-8f, -8f, 8f, -4f)), new Room("ocean", Box(-8f, 0f, 8f, 8f))],
            [new Water("ocean-water", "ocean", Box(-8f, 0f, 8f, 4f), 4f)]);
        scene.Slab(-8f);
        scene.Slab(-3.8f);
        Snapshot result = Read(scene, Body(0f, -6f, "cave"), Vector3.UnitX * 2f);
        AssertDry(result);
        Assert.Equal(Space("cave"), result.Point.Space);
    }

    [Theory]
    [InlineData("lower", -4f, "lower-water", -6f, -2f)]
    [InlineData("upper", 3f, "upper-water", 1f, 5f)]
    public void StackedBodiesKeepTheirOwnIntervalAndDomain(string room, float centreY, string domain, float lower, float upper)
    {
        using var scene = new AnalyticMovementEnvironment(
            [new Room("lower", Box(-8f, -7f, 8f, -1f)), new Room("upper", Box(-8f, 0f, 8f, 7f))],
            [new Water("lower-water", "lower", Box(-8f, -6f, 8f, -2f), -2f),
             new Water("upper-water", "upper", Box(-8f, 1f, 8f, 5f), 5f)]);
        Snapshot result = Read(scene, Body(0f, centreY, room), Vector3.UnitX);
        AssertWet(result, domain, false);
        Assert.Equal(lower, result.Point.Interval!.Value.LowerY);
        Assert.Equal(upper, result.Point.Interval.Value.UpperY);
        Assert.All(result.Contacts, contact => Assert.Equal(result.Point.Interval.Value, contact.Interval));
    }

    [Theory]
    [InlineData(2f, false)]
    [InlineData(-2f, true)]
    public void PartialFloodingSeparatesAirFromTheLocalWaterInterval(float centreY, bool wet)
    {
        using var scene = OrdinaryWater();
        Snapshot result = Read(scene, Body(0f, centreY), Vector3.UnitX);
        if (wet) AssertWet(result, "water", false); else AssertDry(result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void VerticalEntryAndExitTraceBothCapsuleExtents(bool descending)
    {
        using var scene = OrdinaryWater();
        Snapshot result = Read(scene, Body(0f, descending ? 2f : -2f), new Vector3(0f, descending ? -4f : 4f, 0f));
        Assert.Equal(!descending, result.Point.InWater);
        // Independent vertical geometry: bottom reaches Y=0, then top reaches Y=0.
        float first = (2f - HalfHeight - Skin) / 4f;
        float covered = (2f + HalfHeight + Skin) / 4f;
        AssertCuts(result, 0f, first, covered, 1f);
        foreach (MovementCoverageSpan span in result.Spans)
        {
            float t = (span.EnterFraction + span.ExitFraction) * 0.5f;
            if (span.ExitFraction > span.EnterFraction)
            {
                Assert.Equal(descending ? t >= first : t <= covered, span.ContactCount > 0);
                Assert.Equal(descending ? t < covered : t > first, span.HasDryCoverage);
            }
        }
        Assert.All(result.Contacts, contact => Assert.Equal(Vector3.UnitY, contact.Normal));
    }

    [Fact]
    public void FloodedLowCeilingClipsTheIntervalBelowItsRemoteNominalSurface()
    {
        using var scene = new AnalyticMovementEnvironment([new Room("room", Box(-8f, -4f, 8f, -1f))],
            [new Water("water", "room", Box(-8f, -4f, 8f, 6f), 6f)]);
        scene.Slab(-4f);
        scene.Slab(-0.8f);
        Snapshot result = Read(scene, Body(0f, -2.5f), Vector3.UnitX);
        AssertWet(result, "water", false);
        MovementWaterInterval interval = result.Point.Interval!.Value;
        Assert.Equal(-1f, interval.UpperY);
        Assert.Equal(6f, interval.NominalSurfaceY);
        Assert.False(interval.UpperIsFreeSurface);
        Assert.All(result.Contacts, contact => Assert.Equal(interval, contact.Interval));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OnlyTheDeclaredShaftConnectionExposesItsFreeSurface(bool connected)
    {
        using var scene = new AnalyticMovementEnvironment(
            [new Room("cave", Box(-4f, -6f, 4f, -1f)), new Room("shaft", Box(-1f, -1f, 1f, 8f))],
            [new Water("connected-water", "cave", Box(-4f, -6f, 4f, 6f), 6f),
             new Water("connected-water", "shaft", Box(-1f, -1f, 1f, 6f), 6f)],
            connected ? [new Link("cave", "shaft")] : []);
        Snapshot result = Read(scene, Body(0f, 2f, "cave"), Vector3.UnitY);
        if (!connected)
        {
            Assert.Equal(MovementAvailability.Unresolved, result.Point.Availability);
            Assert.Equal(MovementAvailability.Unresolved, result.Result.Availability);
            Assert.Empty(result.Spans);
            Assert.Empty(result.Contacts);
            return;
        }
        AssertWet(result, "connected-water", false);
        Assert.Equal(Space("shaft"), result.Point.Space);
        Assert.True(result.Point.Interval!.Value.UpperIsFreeSurface);
        Assert.Equal(6f, result.Point.Interval.Value.UpperY);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BridgeDeckAndBelowDeckWaterUseTheirActualVerticalDomains(bool above)
    {
        using var scene = new AnalyticMovementEnvironment(
            [new Room("deck", Box(-8f, 0f, 8f, 8f)), new Room("under", Box(-8f, -4f, 8f, 0f))],
            [new Water("river", "under", Box(-8f, -4f, 8f, 0f), 0f)]);
        scene.Slab(-4f);
        scene.Slab(0.25f, 0.125f);
        MovementBodyQuery body = Body(0f, above ? 1f : -1.5f, above ? "deck" : "under");
        Snapshot result = Read(scene, body, Vector3.UnitX);
        if (above)
        {
            AssertDry(result);
            var contacts = new CapsuleContact[16];
            CapsuleContactResult solid = scene.Physics.QueryCapsuleContacts(
                new CapsuleShape(Radius, 2f * (HalfHeight - Radius)), Pose.At(body.Centre), 0.002f, contacts);
            Assert.True(solid.Complete);
            Assert.Contains(contacts.Take(solid.Written), contact => contact.Normal.Y > 0.99f);
        }
        else AssertWet(result, "river", false);
    }

    [Fact]
    public void ADryCentreStillReportsAWetOuterCapsuleSide()
    {
        using var scene = Shore();
        Snapshot result = Read(scene, Body(-0.2f, -1f), Vector3.Zero);
        Assert.False(result.Point.InWater);
        AssertKnown(result);
        Assert.All(result.Spans, span => { Assert.True(span.HasDryCoverage); Assert.True(span.ContactCount > 0); });
        Assert.All(result.Contacts, contact => Assert.Equal(-Vector3.UnitX, contact.Normal));
        Assert.All(result.Contacts, contact => Assert.Equal("water/x-min", contact.BoundaryId));
    }

    [Theory]
    [InlineData(0.75f, false)]
    [InlineData(0.6f, true)]
    public void RoundedLowerCapIsNotAnInfiniteVerticalDisc(float centreY, bool overlaps)
    {
        using var scene = Shore();
        Snapshot result = Read(scene, Body(-0.2f, centreY), Vector3.Zero);
        Assert.False(result.Point.InWater);
        AssertKnown(result);
        // Distance to the rectangle corner after removing the 0.5m cylindrical half-length.
        double distance = Math.Sqrt(0.2d * 0.2d + (centreY - 0.5d) * (centreY - 0.5d));
        Assert.Equal(overlaps, distance <= Radius + Skin);
        Assert.All(result.Spans, span => Assert.Equal(overlaps, span.ContactCount > 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdjacentDomainsHaveHalfOpenCentreOwnershipAndSimultaneousFootprintCoverage(bool reverse)
    {
        Water a = new("a", "room", Box(-6f, -4f, 0f, 0f), 0f);
        Water b = new("b", "room", Box(0f, -4f, 6f, 0f), 0f);
        using var scene = new AnalyticMovementEnvironment([new Room("room", RoomBounds)], reverse ? [b, a] : [a, b]);
        Snapshot result = Read(scene, Body(0f, -1f), Vector3.Zero);
        AssertKnown(result);
        Assert.Equal(Domain("b"), result.Point.Domain);
        Assert.All(result.Spans, span =>
        {
            Assert.False(span.HasDryCoverage);
            Assert.Equal(new[] { "a", "b" }, result.Contacts.Skip(span.ContactStart).Take(span.ContactCount)
                .Select(contact => contact.Domain.LocalId).ToArray());
        });
    }

    [Fact]
    public void ObliqueShoreEntryPreservesEveryCoverageInterval()
    {
        using var scene = Shore();
        Snapshot result = Read(scene, Body(-1f, -2f), new Vector3(4f, 0.5f, 0f));
        float enter = (1f - Radius - Skin) / 4f;
        float entirelyWet = (1f + Radius + Skin) / 4f;
        AssertCuts(result, 0f, enter, entirelyWet, 1f);
        foreach (MovementCoverageSpan span in result.Spans)
        {
            if (span.ExitFraction == span.EnterFraction) continue;
            float t = (span.EnterFraction + span.ExitFraction) * 0.5f;
            Assert.Equal(t >= enter, span.ContactCount > 0);
            Assert.Equal(t < entirelyWet, span.HasDryCoverage);
        }
        Assert.All(result.Contacts, contact => Assert.Equal(-Vector3.UnitX, contact.Normal));
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(0f)]
    [InlineData(1f)]
    public void ClosedStartTangencySurvivesStationaryInwardAndOutwardPaths(float direction)
    {
        using var scene = Shore();
        Snapshot result = Read(scene, Body(-(Radius + Skin), -1f), Vector3.UnitX * direction);
        AssertKnown(result);
        Assert.False(result.Point.InWater);
        Assert.Equal(0f, result.Spans[0].EnterFraction);
        Assert.Equal(0f, result.Spans[0].ExitFraction);
        Assert.Equal(1, result.Spans[0].ContactCount);
        Assert.All(result.Contacts, contact => { Assert.Equal(0f, contact.Fraction); Assert.Equal(-Vector3.UnitX, contact.Normal); });
        Assert.All(result.Spans.Where(span => span.ExitFraction > span.EnterFraction),
            span => Assert.Equal(direction >= 0f, span.ContactCount > 0));
    }

    [Fact]
    public void FinalEndpointTangencyIsNotLostByHalfOpenPathSpans()
    {
        using var scene = Shore();
        float extent = Radius + Skin;
        Snapshot result = Read(scene, Body(-2f * extent, -1f), Vector3.UnitX * extent);
        AssertCuts(result, 0f, 1f);
        Assert.All(result.Spans.Where(span => span.ExitFraction > span.EnterFraction), span => Assert.Equal(0, span.ContactCount));
        MovementCoverageSpan endpoint = result.Spans[^1];
        Assert.Equal(1f, endpoint.EnterFraction);
        Assert.Equal(1f, endpoint.ExitFraction);
        Assert.Equal(1, endpoint.ContactCount);
        Assert.Equal(1f, result.Contacts[endpoint.ContactStart].Fraction);
    }

    [Fact]
    public void PhysicalPartitionSeamDoesNotCreateAShoreOrChangePortableIdentity()
    {
        using var whole = OrdinaryWater();
        using var split = new AnalyticMovementEnvironment([new Room("room", RoomBounds)],
            [new Water("water", "room", Box(-6f, -4f, 0f, 0f), 0f),
             new Water("water", "room", Box(0f, -4f, 6f, 0f), 0f)]) { BackingIds = ["left-page", "right-page"] };
        Snapshot a = Read(whole, Body(-1f, -1f), Vector3.UnitX * 2f);
        Snapshot b = Read(split, Body(-1f, -1f), Vector3.UnitX * 2f);
        AssertWet(a, "water", false);
        AssertWet(b, "water", false);
        Assert.Equal(a.Result.Identity, b.Result.Identity);
        Assert.Equal(a.Spans, b.Spans);
        Assert.Equal(a.Contacts, b.Contacts);
    }

    [Fact]
    public void MissingContainmentCannotPublishDrySpaceOrEitherBuffer()
    {
        using var scene = OrdinaryWater();
        scene.MissingContainment = true;
        using var lease = scene.Acquire("room");
        MovementBodyQuery body = Body(0f, -1f);
        Assert.Equal(MovementAvailability.Unresolved, lease.SampleCentreWater(body).Availability);
        MovementCoverageSpan sentinel = new(0.5f, 1f, 0, 1, true);
        MovementDomainContact contact = new(Domain("sentinel"), Space("room"), new(-4f, 0f, 0f, true, "low", "top"),
            Vector2.Zero, Vector3.UnitZ, 0.75f, "sentinel", 123u);
        MovementCoverageSpan[] spans = [sentinel];
        MovementDomainContact[] contacts = [contact];
        MovementCoverageResult result = lease.TraceWater(new(body, Vector3.UnitX), spans, contacts);
        Assert.Equal(MovementAvailability.Unresolved, result.Availability);
        Assert.Equal(sentinel, spans[0]);
        Assert.Equal(contact, contacts[0]);
    }

    [Fact]
    public void AnalyticPathCapacityRefusesWithoutPublishingItsFirstSpan()
    {
        using var scene = OrdinaryWater();
        using var lease = scene.Acquire("room");
        MovementCoverageSpan sentinel = new(0.5f, 1f, 0, 0, true);
        MovementCoverageSpan[] spans = [sentinel];
        var contacts = new MovementDomainContact[32];
        MovementCoverageResult result = lease.TraceWater(new(Body(0f, 2f), new Vector3(0f, -4f, 0f)), spans, contacts);
        Assert.Equal(MovementAvailability.CapacityExceeded, result.Availability);
        Assert.True(result.RequiredSpanCapacity > spans.Length);
        Assert.Equal(sentinel, spans[0]);
        Assert.All(contacts, contact => Assert.Equal(default(MovementDomainContact), contact));
    }

    sealed record Snapshot(MovementWaterPoint Point, MovementCoverageResult Result,
        MovementCoverageSpan[] Spans, MovementDomainContact[] Contacts);

    static Snapshot Read(AnalyticMovementEnvironment scene, MovementBodyQuery body, Vector3 delta)
    {
        using var lease = scene.Acquire(body.CurrentSpace.LocalId);
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        MovementWaterPoint point = lease.SampleCentreWater(body);
        MovementCoverageResult result = lease.TraceWater(new(body, delta), spans, contacts);
        return new(point, result, spans[..result.SpansWritten], contacts[..result.ContactsWritten]);
    }

    static void AssertKnown(Snapshot result)
    {
        Assert.Equal(MovementAvailability.Known, result.Result.Availability);
        Assert.Equal(Identity, result.Result.Identity);
        Assert.InRange(result.Result.CertifiedErrorMetres, 0f, Skin);
        float next = 0f;
        foreach (MovementCoverageSpan span in result.Spans)
        {
            Assert.Equal(next, span.EnterFraction);
            next = span.ExitFraction;
            Assert.True(span.IsValid);
            Assert.InRange(span.ContactStart + span.ContactCount, 0, result.Contacts.Length);
        }
        Assert.Equal(1f, next);
        Assert.All(result.Contacts, contact => Assert.True(contact.IsValid));
    }

    static void AssertDry(Snapshot result)
    {
        AssertKnown(result);
        Assert.Equal(MovementAvailability.Known, result.Point.Availability);
        Assert.False(result.Point.InWater);
        Assert.Empty(result.Contacts);
        Assert.All(result.Spans, span => { Assert.True(span.HasDryCoverage); Assert.Equal(0, span.ContactCount); });
    }

    static void AssertWet(Snapshot result, string domain, bool dryCoverage)
    {
        AssertKnown(result);
        Assert.True(result.Point.InWater);
        Assert.Equal(Domain(domain), result.Point.Domain);
        Assert.All(result.Spans, span => { Assert.Equal(dryCoverage, span.HasDryCoverage); Assert.True(span.ContactCount > 0); });
        Assert.All(result.Contacts, contact => Assert.Equal(Domain(domain), contact.Domain));
    }

    static void AssertCuts(Snapshot result, params float[] expected)
    {
        AssertKnown(result);
        float[] actual = result.Spans.SelectMany(span => new[] { span.EnterFraction, span.ExitFraction }).Distinct().ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++) Assert.InRange(actual[i], expected[i] - 0.00001f, expected[i] + 0.00001f);
    }

    static MovementBodyQuery Body(float x, float y, string room = "room") => new(new Vector3(x, y, 0f), Radius, HalfHeight, Space(room), null);
    static AnalyticBox Box(float x0, float y0, float x1, float y1) => new(new Vector3(x0, y0, -6f), new Vector3(x1, y1, 6f));
    static AnalyticMovementEnvironment OrdinaryWater() => new([new Room("room", RoomBounds)],
        [new Water("water", "room", Box(-6f, -4f, 6f, 0f), 0f)]);
    static AnalyticMovementEnvironment Shore() => new([new Room("room", RoomBounds)],
        [new Water("water", "room", Box(0f, -4f, 6f, 0f), 0f)]);
}
