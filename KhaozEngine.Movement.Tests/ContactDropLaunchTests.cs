using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropLaunchTests
{
    [Fact]
    public void LiveBlockerBeforeLaunchReturnsBlockedWithoutDeparting()
    {
        using var fixture = new ContactDropFixture();
        var tuning = ContactDropFixture.Tuning;
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.Contains(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
        fixture.Scene.Registration.ApplyRuntimeStatics(new[] { RuntimeStaticChange.Set(new("world", "live-roof"),
            new BoxShape(new(0.2f, 0.2f, 0.5f)), Pose.At(new(0.4f, 3, 0))) });
        var move = new MoveToRange(nav);
        var body = new MoveState { Position = new(-0.75f, 4.75f, 0), Grounded = true };
        var target = ReachTarget.Point(new(1.25f, 0.75f, 0));
        RangeSteering steering = default;
        for (int tick = 0; tick < 60; tick++)
        {
            steering = move.Tick(body, tuning, target, 0.01f, false, 1f / 30, fixture.Context);
            body = NpcGroundMovement.Step(body, steering, false, 1f / 30, tuning, fixture.Context);
            Assert.True(body.Grounded);
            Assert.Equal(4.75f, body.Position.Y);
            if (steering.Status == RangeMoveStatus.Blocked) break;
        }
        Assert.Equal(RangeMoveStatus.Blocked, steering.Status);
        Assert.Equal(Vector2.Zero, steering.WorldDirection);
    }

    [Fact]
    public void LivePreviewRejectsTransientSwimmingDespiteDrySupportedEndState()
    {
        var tuning = ContactDropFixture.Tuning with { Water = new(WaterTraversalMode.SurfaceSwimmer) };
        using var fixture = new ContactDropFixture(tuning: tuning, water: MovementWaterField.Level("world", 0.985f, 0.01f));
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        NavLink link = Assert.Single(nav.Space.Links, link => link.Kind == NavLinkKind.Drop &&
            link.FromX == 2 && link.FromZ == 1 && link.ToX == 3 && link.ToZ == 1);
        var traversal = new NavDropTraversal(link, ContactDropProof.Feet(nav.Space.Layers[link.FromLayer], link.FromX, link.FromZ),
            ContactDropProof.Feet(nav.Space.Layers[link.ToLayer], link.ToX, link.ToZ));
        var body = new MoveState { Position = traversal.StartFeet + Vector3.UnitY * tuning.CapsuleHalfHeight, Grounded = true };
        Assert.Equal(RangeMoveStatus.Blocked, ContactDropPreview.Prove(nav, traversal, body, tuning, false, 1f / 30,
            fixture.Context, new ContactDropPredictionBasis()));
    }

    [Fact]
    public void RootedDropAlignmentDoesNotLatchGroundStall()
    {
        using var fixture = new ContactDropFixture();
        var tuning = ContactDropFixture.Tuning;
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.Contains(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
        var move = new MoveToRange(nav, null, new RouteApproachOptions { Stall = new RouteStallOptions(3, 0.01f) });
        var body = new MoveState { Position = new(-0.25f, 4.75f, 0), Grounded = true, SpeedScale = 0 };
        var target = ReachTarget.Point(new(1.25f, 0.75f, 0));
        for (int tick = 0; tick < 10; tick++)
        {
            var held = move.Tick(body, tuning, target, 0.01f, false, 1f / 30, fixture.Context);
            Assert.Equal(RangeMoveStatus.Following, held.Status);
            Assert.Equal(Vector2.Zero, held.WorldDirection);
        }
    }

    [Theory]
    [InlineData("body")]
    [InlineData("scale")]
    [InlineData("run")]
    [InlineData("dt")]
    public void ChangedExecutionBasisDoesNotReuseLaunchPreview(string changed)
    {
        using var fixture = new ContactDropFixture();
        var tuning = ContactDropFixture.Tuning;
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        var move = new MoveToRange(nav);
        var body = new MoveState { Position = new(-0.75f, 4.75f, 0), Grounded = true };
        var target = ReachTarget.Point(new(1.25f, 0.75f, 0));
        for (int tick = 0; tick < 60 && body.Grounded; tick++)
        {
            var steering = move.Tick(body, tuning, target, 0.01f, false, 1f / 30, fixture.Context);
            body = NpcGroundMovement.Step(body, steering, false, 1f / 30, tuning, fixture.Context);
        }
        Assert.False(body.Grounded);
        if (changed == "body") body.HorizontalVelocity += new Vector2(0, 1);
        if (changed == "scale") body.SpeedScale = 0.5f;
        var refused = move.Tick(body, tuning, target, 0.01f, changed == "run", changed == "dt" ? 1f / 60 : 1f / 30, fixture.Context);
        Assert.Equal(RangeMoveStatus.Suspended, refused.Status);
        Assert.Equal(Vector2.Zero, refused.WorldDirection);
    }
}
