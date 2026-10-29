using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropObsoleteRouteTests
{
    const float Dt = 1f / 30;

    [Fact]
    public void TargetMovedOntoTheLedgeRefreshesBeforeTheObsoleteDrop()
    {
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
        using var capture = PhysicsNavBake.Capture(context, new(-7.5f, -1.25f, 2.5f, 1.25f,
            0.5f, 10, 18, tuning.MaxSlopeRadians, 128, 512), _ => 0);
        var nav = capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });

        // Sweep the start across one float step so arrival at the exact source cell centre is not luck.
        foreach (float startX in new[] { -3.25f, MathF.BitDecrement(-3.25f), MathF.BitIncrement(-3.25f) })
            Approach(startX, tuning, nav, context);
    }

    static void Approach(float startX, MoveTuning tuning, GroundNavigation nav, GroundMoveContext context)
    {
        // The cooldown outlasts the whole walk, so only the pending Drop can make the due query immediate.
        var move = new MoveToRange(nav, new PathFollowConfig { ReplanCooldownSeconds = 30 });
        var follower = Assert.IsType<PathFollower>(typeof(MoveToRange).GetField("_follower",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(move));
        FieldInfo plannerField = typeof(PathFollower).GetField("_planner", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var planner = new Recorder(Assert.IsAssignableFrom<IRegionPathPlanner>(plannerField.GetValue(follower)));
        plannerField.SetValue(follower, planner);

        var body = new MoveState { Position = new(startX, 4.75f, 0), Grounded = true };
        var below = ReachTarget.Point(new(0.25f, 0.75f, 0));
        var behind = ReachTarget.Point(new(-6.25f, 4.75f, 0));
        RangeSteering first = move.Tick(body, tuning, below, 0.01f, false, Dt, context);
        Assert.Equal(RangeMoveStatus.Following, first.Status);
        Assert.True(first.WorldDirection.X > 0);
        Assert.Equal(1, planner.Calls);
        Assert.Contains(follower.ActivePath!.Waypoints, w => w.Kind == NavWaypointKind.Drop);
        body = NpcGroundMovement.Step(body, first, false, Dt, tuning, context);

        int? refreshed = null;
        for (int tick = 1; tick < 150; tick++)
        {
            RangeSteering steering = move.Tick(body, tuning, behind, 0.01f, false, Dt, context);
            string at = $"start {startX:R}, tick {tick}, body {body.Position}";
            Assert.NotEqual(RangeMoveStatus.Dropping, steering.Status);
            if (steering.Status == RangeMoveStatus.InRange)
            {
                Assert.NotNull(refreshed);
                Assert.Equal(2, planner.Calls);
                return;
            }
            Assert.True(steering.Status == RangeMoveStatus.Following, at);
            if (refreshed is null && planner.Calls == 2)
            {
                refreshed = tick;
                Assert.Equal(4f, planner.LastStart.Y);
                Assert.DoesNotContain(follower.ActivePath!.Waypoints, w => w.Kind == NavWaypointKind.Drop);
            }
            Assert.Equal(refreshed is null ? 1 : 2, planner.Calls);
            Assert.True(refreshed is null ? steering.WorldDirection.X > 0 : steering.WorldDirection.X < 0, at);
            body = NpcGroundMovement.Step(body, steering, false, Dt, tuning, context);
            Assert.True(body.Grounded, at);
            Assert.Equal(4.75f, body.Position.Y);
        }
        Assert.Fail($"Start {startX:R} never returned to the moved target on the ledge.");
    }

    sealed class Recorder(IRegionPathPlanner inner) : IRegionPathPlanner
    {
        public int Calls { get; private set; }
        public Vector3 LastStart { get; private set; }
        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)
        {
            Calls++;
            LastStart = start;
            return inner.FindPath(start, goal, agentRadius, budget);
        }
        public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget)
            => throw new InvalidOperationException("Range steering must use region queries.");
    }
}
