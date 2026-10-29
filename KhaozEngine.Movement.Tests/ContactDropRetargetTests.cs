using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropRetargetTests
{
    [Fact]
    public void LandingTickReplansMovedGoalBeforeFollowingNextDrop()
    {
        const float dt = 1f / 30, cooldown = 0.5f;
        var tuning = ContactDropFixture.Tuning;
        using var scene = new OrdinaryContactScene(tuning, (x, _) => x < 0 ? 4 : x < 1 ? 0 : -4,
            extent: new(new(-20, -10, -20), new(20, 12, 20), -4, 10),
            install: (world, builder) =>
            {
                ContactNavigationFixture.Box(world, builder, "floor", new(-8, -5, -8), new(8, -4, 8));
                ContactNavigationFixture.Box(world, builder, "middle", new(-8, -4, -8), new(1, 0, 8));
                ContactNavigationFixture.Box(world, builder, "upper", new(-8, 0, -8), new(0, 4, 8));
            });
        using var context = ContactNavigationFixture.Context(scene);
        using var capture = PhysicsNavBake.Capture(context, new(-1.5f, -1.25f, 2.5f, 1.25f,
            0.5f, 10, 18, tuning.MaxSlopeRadians, 128, 512), _ => 0);
        var nav = capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        var move = new MoveToRange(nav, new PathFollowConfig
        {
            ReplanCooldownSeconds = cooldown, GoalRetargetTolerance = 0.1f,
        });
        var follower = Assert.IsType<PathFollower>(typeof(MoveToRange).GetField("_follower",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(move));
        var body = new MoveState { Position = new(-0.25f, 4.75f, 0), Grounded = true };
        var target = ReachTarget.Point(new(1.25f, -3.25f, 0));
        var latest = ReachTarget.Point(new(0.25f, 0.75f, 1));
        bool flew = false, moved = false;
        float dropSeconds = 0, flightSeconds = 0;
        NavPath? locked = null;
        for (int tick = 0; tick < 120; tick++)
        {
            if (!body.Grounded) { flew = true; moved = true; target = latest; flightSeconds += dt; }
            var steering = move.Tick(body, tuning, target, 0.01f, false, dt, context);
            if (flew && body.Grounded)
            {
                Assert.True(moved);
                Assert.Equal(0.75f, body.Position.Y);
                Assert.True(dropSeconds > cooldown);
                Assert.True(flightSeconds > cooldown);
                Assert.Equal(RangeMoveStatus.Following, steering.Status);
                Assert.NotSame(locked, follower.ActivePath);
                Assert.DoesNotContain(follower.ActivePath!.Waypoints, w => w.Kind == NavWaypointKind.Drop);
                Assert.True(steering.WorldDirection.Y > 0);
                return;
            }
            Assert.Equal(RangeMoveStatus.Dropping, steering.Status);
            locked ??= follower.ActivePath;
            Assert.Same(locked, follower.ActivePath);
            Assert.Equal(2, locked!.Waypoints.Count(w => w.Kind == NavWaypointKind.Drop));
            dropSeconds += dt;
            body = NpcGroundMovement.Step(body, steering, false, dt, tuning, context);
        }
        Assert.Fail("The real controller never reached the first supported landing.");
    }
}
