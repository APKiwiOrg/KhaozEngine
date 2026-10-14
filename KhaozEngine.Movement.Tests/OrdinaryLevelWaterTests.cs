using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class OrdinaryLevelWaterTests
{
    [Theory]
    [InlineData(0.75f, 0f)]
    [InlineData(-4.75f, 0f)]
    [InlineData(2f, -4f)]
    [InlineData(-2f, 4f)]
    [InlineData(-1f, 0f)]
    public void LevelTraceIsCompleteAndCapacityRefusalHasNoPrefix(float centreY, float deltaY)
    {
        var field = MovementWaterField.Level("world", 0, -4);
        var identity = new MovementQueryIdentity("ordinary-contact-v1", 1, "test");
        var body = new MovementBodyQuery(new(0, centreY, 0), 0.25f, 0.75f, new("world", "ordinary"), null);
        var query = new MovementMediumSweepQuery(body, new(1, deltaY, 1));
        var spans = new MovementCoverageSpan[64];
        var contacts = new MovementDomainContact[256];
        var result = field.TraceWater(query, Vector3.Zero, identity, spans, contacts);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(0, spans[0].EnterFraction);
        Assert.Equal(1, spans[result.SpansWritten - 1].ExitFraction);
        Assert.InRange(result.CertifiedErrorMetres, 0f, 0.001f);
        Assert.Equal(identity, result.Identity);
        var spanSentinels = spans.ToArray();
        var contactSentinels = contacts.ToArray();
        var tooSmall = field.TraceWater(query, Vector3.Zero, identity, spans.AsSpan(0, 0), contacts);
        Assert.Equal(MovementAvailability.CapacityExceeded, tooSmall.Availability);
        Assert.Equal(0, tooSmall.SpansWritten);
        Assert.Equal(0, tooSmall.ContactsWritten);
        Assert.Equal(spanSentinels, spans);
        Assert.Equal(contactSentinels, contacts);
        if (result.ContactsWritten > 0)
        {
            tooSmall = field.TraceWater(query, Vector3.Zero, identity, spans, contacts.AsSpan(0, result.ContactsWritten - 1));
            Assert.Equal(MovementAvailability.CapacityExceeded, tooSmall.Availability);
            Assert.Equal(0, tooSmall.SpansWritten);
            Assert.Equal(0, tooSmall.ContactsWritten);
            Assert.Equal(spanSentinels, spans);
            Assert.Equal(contactSentinels, contacts);
        }
        for (int i = 1; i < result.SpansWritten; i++)
            Assert.Equal(spans[i - 1].ExitFraction, spans[i].EnterFraction);
        if ((centreY is 0.75f or -4.75f) && deltaY == 0)
        {
            Assert.True(result.ContactsWritten > 0);
            Assert.All(contacts.Take(result.ContactsWritten), contact =>
            {
                Assert.Equal(MovementContactOverlap.Tangent, contact.Overlap);
                Assert.Equal(MovementContactOverlap.Tangent, contact.SpanOverlap);
                Assert.Equal(new MovementDomainKey("world", "level"), contact.Domain);
                Assert.Equal(centreY == 0.75f ? "level/surface" : "level/bed", contact.BoundaryId);
            });
        }
    }

    [Fact]
    public void LevelMembershipUsesStrictSurfaceAndFiniteBed()
    {
        var field = MovementWaterField.Level("world", 0, -4);
        MovementWaterPoint Point(float feet) => field.SampleCentreWater(new(new(0, feet + 0.75f, 0),
            0.25f, 0.75f, new("world", "ordinary"), null), Vector3.Zero);
        Assert.False(Point(0).InWater);
        Assert.True(Point(-4).InWater);
        Assert.False(Point(-4.01f).InWater);
        Assert.Equal(MovementAvailability.Invalid, field.SampleCentreWater(new(new(0), 0.25f, 0.75f,
            new("other", "ordinary"), null), Vector3.Zero).Availability);
        Assert.Throws<ArgumentException>(() => MovementWaterField.Level(" ", 0, -4));
        Assert.Throws<ArgumentException>(() => MovementWaterField.Level("world", -4, -4));
        Assert.Throws<ArgumentException>(() => MovementWaterField.Level("world", float.PositiveInfinity, -4));
        var wrong = new MovementMediumSweepQuery(new(new(0), 0.25f, 0.75f, new("other", "ordinary"), null), Vector3.Zero);
        Assert.Equal(MovementAvailability.Invalid, field.TraceWater(wrong, Vector3.Zero, new("test", 1, "wrong"), [], []).Availability);
    }

    [Theory]
    [InlineData(WaterTraversalMode.SurfaceSwimmer)]
    [InlineData(WaterTraversalMode.WadeOnly)]
    [InlineData(WaterTraversalMode.DryOnly)]
    public void SurfaceEntryBuoyancyHopAndForbiddenStartsUseZeroOffset(WaterTraversalMode mode)
    {
        var tuning = OrdinaryContactScene.DefaultTuning with { Water = new(mode, 7) };
        using var scene = new OrdinaryContactScene(tuning, (_, _) => -4,
            extent: new(new(-20, -4, -20), new(20, 4, 20), -4, -4), water: MovementWaterField.Level("world", 0, -4));
        using var lease = scene.Acquire();
        const float dt = 1f / 30f;
        var submerged = scene.State(new(0, -1, 0), false);
        var entry = scene.Binding.StepTowards(submerged, Vector2.Zero, false, dt, lease);
        if (mode != WaterTraversalMode.SurfaceSwimmer)
        {
            Assert.Equal(MovementStepOutcome.PlacementRefused, entry.Outcome);
            Assert.Equal(submerged.State.Position, entry.State.State.Position);
            Assert.False(entry.State.State.Swimming);
            return;
        }
        Assert.Equal(MovementStepOutcome.Advanced, entry.Outcome);
        Assert.True(entry.State.State.Swimming);
        Assert.True(entry.State.State.Position.Y > submerged.State.Position.Y);
        var atSurface = new MoveState
        {
            Position = new(0, -0.15f, 0),
            Swimming = true,
            WaterExcursion = WaterExcursionState.Surface
        };
        var hop = scene.Binding.Step(new(atSurface, scene.Frame, null), new(Vector2.Zero, false, 0, jump: true), dt, lease);
        Assert.Equal(WaterExcursionState.AirborneFromWater, hop.State.State.WaterExcursion);
        Assert.Equal((float)(7d - (double)tuning.Gravity * dt), hop.State.State.VerticalVelocity);
        var exited = scene.Binding.StepTowards(scene.State(new(0, 1, 0), false), Vector2.Zero, false, dt, lease);
        Assert.False(exited.State.State.Swimming);
    }

    [Fact]
    public void DryNullFieldCertifiesOneAllDrySpanAndKnownDryPoint()
    {
        using var scene = new OrdinaryContactScene();
        using var lease = scene.Acquire();
        Assert.Equal(MovementAvailability.Known, lease.RebuildSelection(scene.State(new(0, 0.75f, 0)), out var selection));
        var body = new MovementBodyQuery(new(0, 0.75f, 0), 0.25f, 0.75f, selection.Space, null);
        var point = lease.SampleCentreWater(body);
        Assert.Equal(MovementAvailability.Known, point.Availability);
        Assert.False(point.InWater);
        var spans = new MovementCoverageSpan[1];
        var result = lease.TraceWater(new(body, Vector3.Zero), spans, []);
        Assert.Equal(MovementAvailability.Known, result.Availability);
        Assert.Equal(1, result.SpansWritten);
        Assert.Equal(new MovementCoverageSpan(0, 1, 0, 0, true), spans[0]);
    }
}
