using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropPlayerInputTests
{
    [Theory]
    [InlineData(0.7f, false, 3f)]
    [InlineData(-1.3f, false, 3f)]
    [InlineData(0.7f, true, 4f)]
    public void CameraRelativePlayerDropKeepsOwnershipThroughFractionalLandingInput(float yaw, bool rotate, float pace)
    {
        const float dt = 1f / 30;
        var tuning = ContactDropFixture.Tuning with { WalkSpeed = pace, AirMomentum = true, AirBrakeAccel = 100 };
        using var fixture = new ContactDropFixture(height: 1, tuning: tuning);
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        Assert.Contains(nav.Graph.Links, link => link.Kind == NavLinkKind.Drop);
        var move = new MoveToRange(nav, new PathFollowConfig { AcceptRadius = 1 }, new RouteApproachOptions
        {
            StraightenRoutes = true, CarryThroughStraightRuns = true,
            Stall = new RouteStallOptions(8, 0.01f),
        });
        var body = new MoveState { Position = new(-1.25f, 1.75f, 0), Grounded = true, HorizontalVelocity = new(0.3f, 0) };
        var target = ReachTarget.Point(new(1.25f, 0.75f, 0));
        var statuses = new List<RangeMoveStatus>();
        var commands = new List<string>();
        bool flew = false, landed = false, fractionalLanding = false;
        for (int tick = 0; tick < 240; tick++)
        {
            var input = new PlayerPathInput(rotate ? yaw + tick * 0.1f : yaw);
            RangeSteering steering = move.Tick(body, tuning, target, 0.01f, false, dt, fixture.Context, input);
            statuses.Add(steering.Status);
            MoveCommand command = input.Command(steering, false);
            MoveState before = body;
            body = Step(body, command, dt, fixture.Context);
            fractionalLanding |= steering.Status == RangeMoveStatus.Dropping && !before.Grounded && !body.Grounded &&
                body.Position.Y < 1.75f && command.Move.Length() is > 0 and < 1;
            if (steering.Status == RangeMoveStatus.Dropping)
                commands.Add($"{tick}: {before.Position} grounded={before.Grounded}, axis={command.Move.Length()}, after={body.Position} grounded={body.Grounded}");
            flew |= !body.Grounded;
            landed |= flew && body.Grounded && body.Position.Y == tuning.CapsuleHalfHeight;
            if (steering.Status == RangeMoveStatus.InRange) break;
        }
        Assert.DoesNotContain(RangeMoveStatus.Suspended, statuses);
        Assert.Contains(RangeMoveStatus.Dropping, statuses);
        Assert.True(fractionalLanding, string.Join('\n', commands));
        Assert.True(flew);
        Assert.True(landed);
        Assert.Equal(RangeMoveStatus.InRange, statuses[^1]);
        Assert.True(body.Grounded);
        Assert.False(body.Swimming);
        Assert.True(ReachGeometry.Within(new(body.Position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight), target, 0.01f));
    }

    static MoveState Step(MoveState body, MoveCommand command, float dt, GroundMoveContext context)
    {
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
