using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.ContactBakeFile;

namespace KhaozEngine.Tests.Movement;

public class ContactDropRangeTests
{
    [Theory]
    [InlineData(false, false, false, 30, 1f, 1f)]
    [InlineData(false, true, true, 60, 0.5f, 4f)]
    [InlineData(true, false, true, 30, 1f, 4f)]
    [InlineData(true, true, false, 60, 1f, 6f)]
    public void BakedRegionRouteAndRealHostCommandsReachSupportedLowerRange(bool player, bool run, bool loaded,
        int hz, float speedScale, float height)
    {
        MoveTuning tuning = ContactDropFixture.Tuning with { AirMomentum = true, AirBrakeAccel = 100 };
        using var fixture = new ContactDropFixture(height, tuning);
        var profile = new NavBakeProfile("player", tuning, default) { MaxDropMetres = 6 };
        var fresh = GroundNavigationBake.Create(fixture.Capture, Sources(), [profile]);
        GroundNavigation nav = (loaded ? Load(Write(fresh), new(ContactDropFixture.Options, Sources(), [profile])) : fresh).GetProfile("player");
        Assert.Contains(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
        var move = new MoveToRange(nav, new PathFollowConfig { AcceptRadius = 1 }, new RouteApproachOptions
        {
            StraightenRoutes = true, CarryThroughStraightRuns = true,
            Stall = new RouteStallOptions(8, 0.01f),
        });
        var body = new MoveState { Position = new(-1.25f, height + tuning.CapsuleHalfHeight, 0), Grounded = true, SpeedScale = speedScale, HorizontalVelocity = new(0.3f, 0) };
        var target = ReachTarget.Point(new(1.25f, tuning.CapsuleHalfHeight, 0));
        bool followed = false, dropped = false, flew = false, landed = false;
        RangeSteering steering = default;
        for (int tick = 0; tick < 240; tick++)
        {
            steering = move.Tick(body, tuning, target, 0.01f, run, 1f / hz, fixture.Context);
            Assert.Contains(steering.Status, new[] { RangeMoveStatus.Following, RangeMoveStatus.Dropping, RangeMoveStatus.InRange });
            followed |= steering.Status == RangeMoveStatus.Following;
            dropped |= steering.Status == RangeMoveStatus.Dropping;
            MoveState before = body;
            body = player ? PlayerStep(body, steering, run, 1f / hz, fixture.Context) :
                NpcGroundMovement.Step(body, steering, run, 1f / hz, tuning, fixture.Context);
            flew |= !body.Grounded;
            landed |= flew && body.Grounded && body.Position.Y < tuning.CapsuleHalfHeight + 1;
            if (steering.Status == RangeMoveStatus.InRange)
            {
                Assert.True(before.Grounded);
                Assert.False(before.Swimming);
                break;
            }
        }
        Assert.True(followed);
        Assert.True(dropped);
        Assert.True(flew);
        Assert.True(landed);
        Assert.Equal(RangeMoveStatus.InRange, steering.Status);
        Assert.True(ReachGeometry.Within(new(body.Position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight), target, 0.01f));
        Assert.True(body.Grounded);
        Assert.False(body.Swimming);
    }

    [Fact]
    public void SupportedSegmentGuardDoesNotAdmitAnAcceptedDrop()
    {
        using var fixture = new ContactDropFixture();
        var nav = fixture.Capture.BuildProfile(ContactDropFixture.Tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        NavLink drop = nav.Graph.Links.First(l => l.Kind == NavLinkKind.Drop);
        Assert.True(nav.Graph.CanTraverse(drop.FromLayer, drop.FromX, drop.FromZ, drop.ToLayer, drop.ToX, drop.ToZ));
        Assert.False(nav.AllowsSegment(ContactDropProof.Feet(nav.Space.Layers[drop.FromLayer], drop.FromX, drop.FromZ),
            ContactDropProof.Feet(nav.Space.Layers[drop.ToLayer], drop.ToX, drop.ToZ)));
    }

    [Fact]
    public void UnrelatedAirborneBodyAndBufferedJumpRemainSuspended()
    {
        using var fixture = new ContactDropFixture();
        var tuning = ContactDropFixture.Tuning;
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        var move = new MoveToRange(nav);
        var body = new MoveState { Position = new(-0.75f, 4 + tuning.CapsuleHalfHeight, 0), Grounded = false };
        var target = ReachTarget.Point(new(1.25f, tuning.CapsuleHalfHeight, 0));
        Assert.Equal(RangeMoveStatus.Suspended, move.Tick(body, tuning, target, 0.01f, false, 1f / 30, fixture.Context).Status);
        body.Grounded = true;
        body.JumpBufferRemaining = 0.1f;
        Assert.Equal(RangeMoveStatus.Suspended, move.Tick(body, tuning, target, 0.01f, false, 1f / 30, fixture.Context).Status);
    }

    [Fact]
    public void ResetDuringFlightReleasesTheTraversalAndGravityContinues()
    {
        using var fixture = new ContactDropFixture();
        var tuning = ContactDropFixture.Tuning;
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        var move = new MoveToRange(nav);
        var body = new MoveState { Position = new(-0.75f, 4 + tuning.CapsuleHalfHeight, 0), Grounded = true };
        var target = ReachTarget.Point(new(1.25f, tuning.CapsuleHalfHeight, 0));
        for (int tick = 0; tick < 60 && body.Grounded; tick++)
        {
            var steering = move.Tick(body, tuning, target, 0.01f, false, 1f / 30, fixture.Context);
            body = NpcGroundMovement.Step(body, steering, false, 1f / 30, tuning, fixture.Context);
        }
        Assert.False(body.Grounded);
        move.Reset();
        Assert.Equal(RangeMoveStatus.Suspended, move.Tick(body, tuning, target, 0.01f, false, 1f / 30, fixture.Context).Status);
        MoveState held = NpcGroundMovement.Hold(body, 1f / 30, tuning, fixture.Context);
        Assert.True(held.Position.Y < body.Position.Y);
    }

    internal static MoveState PlayerStep(MoveState body, RangeSteering steering, bool run, float dt, GroundMoveContext context)
    {
        MoveCommand command = PlayerPathMovement.Command(steering, run, 0);
        Assert.False(command.Jump);
        Assert.False(command.FaceCamera);
        Assert.True(command.ScaleSpeedByAxis);
        Assert.Equal(run, command.Run);
        var frame = context.NavigationFrame;
        var binding = context.ResolvedBinding;
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var queries));
        using (queries)
        {
            var result = binding.Step(new(body with { Position = body.Position - frame.PhysicsOrigin }, frame, null), command, dt, queries!);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            return result.State.State with { Position = result.State.State.Position + frame.PhysicsOrigin };
        }
    }
}
