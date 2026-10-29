using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactDropPreparationTests
{
    const float Dt = 1f / 30;
    static readonly ReachTarget Below = ReachTarget.Point(new(1.25f, 0.75f, 0));
    static readonly ReachTarget Behind = ReachTarget.Point(new(-1.25f, 4.75f, 0));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GoalMovedDuringGroundedPreparationRefreshesBeforeLaunch(bool launchPrefix)
    {
        using var fixture = new ContactDropFixture();
        var tuning = ContactDropFixture.Tuning;
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        float start = launchPrefix ? -0.25f : 4.875f;
        foreach (float phase in new[] { start, MathF.BitDecrement(start), MathF.BitIncrement(start) })
        {
            var (move, follower, planner) = Driver(nav);
            // Vertical source alignment remains pending even when the horizontal anchors coincide.
            // Quarter pace keeps the supported launch prefix inside the admitted ground footprint.
            var body = new MoveState
            {
                Position = new(launchPrefix ? phase : -0.25f, launchPrefix ? 4.75f : phase, 0),
                Grounded = true, SpeedScale = launchPrefix ? 0.25f : 1f,
            };
            var first = move.Tick(body, tuning, Below, 0.01f, false, Dt, fixture.Context);
            Assert.Equal(launchPrefix ? RangeMoveStatus.Dropping : RangeMoveStatus.Following, first.Status);
            Assert.Equal(1, planner.Calls);
            Assert.Equal(NavWaypointKind.Drop, follower.ActivePath!.Waypoints[follower.ActiveWaypointIndex].Kind);
            body = NpcGroundMovement.Step(body, first, false, Dt, tuning, fixture.Context);
            Assert.True(body.Grounded);
            Assert.Equal(launchPrefix ? 4.75f : phase, body.Position.Y);

            var refreshed = move.Tick(body, tuning, Behind, 0.01f, false, Dt, fixture.Context);
            Assert.Equal(RangeMoveStatus.Following, refreshed.Status);
            Assert.True(refreshed.WorldDirection.X < 0);
            Assert.Equal(2, planner.Calls);
            Assert.Equal(launchPrefix ? 4f : phase - 0.75f, planner.LastStart.Y);
            Assert.DoesNotContain(follower.ActivePath!.Waypoints, waypoint => waypoint.Kind == NavWaypointKind.Drop);
            FinishOnLedge(move, body, refreshed, tuning, fixture.Context, planner);
        }
    }

    [Fact]
    public void RootedRefreshRetainsFreshDropAndNeverRetargetsItsFlight()
    {
        using var fixture = new ContactDropFixture();
        var tuning = ContactDropFixture.Tuning;
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        var (move, follower, planner) = Driver(nav);
        var body = new MoveState { Position = new(-0.25f, 4.75f, 0), Grounded = true, SpeedScale = 0 };
        var held = move.Tick(body, tuning, Below, 0.01f, false, Dt, fixture.Context);
        Assert.Equal(RangeMoveStatus.Following, held.Status);
        Assert.Equal(1, planner.Calls);
        var latest = ReachTarget.Point(new(1.25f, 0.75f, 0.5f));
        held = move.Tick(body, tuning, latest, 0.01f, false, Dt, fixture.Context);
        Assert.Equal(RangeMoveStatus.Following, held.Status);
        Assert.Equal(Vector2.Zero, held.WorldDirection);
        Assert.Equal(2, planner.Calls);
        NavPath locked = follower.ActivePath!;
        Assert.Contains(locked.Waypoints, waypoint => waypoint.Kind == NavWaypointKind.Drop);
        for (int tick = 0; tick < 10; tick++)
        {
            held = move.Tick(body, tuning, latest, 0.01f, false, Dt, fixture.Context);
            Assert.Equal(RangeMoveStatus.Following, held.Status);
            Assert.Equal(Vector2.Zero, held.WorldDirection);
            Assert.Equal(2, planner.Calls);
            Assert.Same(locked, follower.ActivePath);
        }

        body = body with { SpeedScale = 1 };
        bool flew = false;
        for (int tick = 0; tick < 120; tick++)
        {
            if (flew && body.Grounded)
            {
                Assert.Equal(0.75f, body.Position.Y);
                return;
            }
            if (!body.Grounded) { flew = true; latest = Behind; }
            var steering = move.Tick(body, tuning, latest, 0.01f, false, Dt, fixture.Context);
            Assert.Equal(RangeMoveStatus.Dropping, steering.Status);
            Assert.Equal(2, planner.Calls);
            Assert.Same(locked, follower.ActivePath);
            body = NpcGroundMovement.Step(body, steering, false, Dt, tuning, fixture.Context);
        }
        Assert.Fail("The refreshed Drop never completed its real flight to lower support.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootedPlayerReconsidersMovedGoalBeforeRelease(bool intoRange)
    {
        using var fixture = new ContactDropFixture();
        var tuning = ContactDropFixture.Tuning;
        var nav = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        var (move, follower, planner) = Driver(nav);
        var body = new MoveState { Position = new(-0.25f, 4.75f, 0), Grounded = true, SpeedScale = 0 };
        var input = new PlayerPathInput(0);
        for (int tick = 0; tick < 10; tick++)
        {
            var held = move.Tick(body, tuning, Below, 0.01f, false, Dt, fixture.Context, input);
            Assert.Equal(RangeMoveStatus.Following, held.Status);
            Assert.Equal(Vector2.Zero, held.WorldDirection);
            Assert.Equal(1, planner.Calls);
            Assert.Equal(NavWaypointKind.Drop, follower.ActivePath!.Waypoints[follower.ActiveWaypointIndex].Kind);
            body = PlayerStep(body, held, fixture.Context, input);
            Assert.True(body.Grounded);
            Assert.Equal(new Vector3(-0.25f, 4.75f, 0), body.Position);
        }

        var latest = intoRange ? ReachTarget.Point(body.Position) : Behind;
        var reconsidered = move.Tick(body, tuning, latest, 0.01f, false, Dt, fixture.Context, input);
        Assert.Equal(intoRange ? RangeMoveStatus.InRange : RangeMoveStatus.Following, reconsidered.Status);
        Assert.Equal(Vector2.Zero, reconsidered.WorldDirection);
        Assert.Equal(intoRange ? 1 : 2, planner.Calls);
        if (intoRange) Assert.Null(follower.ActivePath);
        else Assert.DoesNotContain(follower.ActivePath!.Waypoints, waypoint => waypoint.Kind == NavWaypointKind.Drop);

        body = body with { SpeedScale = 1 };
        for (int tick = 0; tick < 60; tick++)
        {
            var steering = move.Tick(body, tuning, latest, 0.01f, false, Dt, fixture.Context, input);
            Assert.Equal(intoRange ? 1 : 2, planner.Calls);
            Assert.NotEqual(RangeMoveStatus.Dropping, steering.Status);
            if (steering.Status == RangeMoveStatus.InRange) return;
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            Assert.True(steering.WorldDirection.X < 0);
            body = PlayerStep(body, steering, fixture.Context, input);
            Assert.True(body.Grounded);
            Assert.Equal(4.75f, body.Position.Y);
        }
        Assert.Fail("The released player never reached the latest goal on the ledge.");
    }

    static void FinishOnLedge(MoveToRange move, MoveState body, RangeSteering steering,
        MoveTuning tuning, GroundMoveContext context, Recorder planner)
    {
        for (int tick = 0; tick < 60; tick++)
        {
            body = NpcGroundMovement.Step(body, steering, false, Dt, tuning, context);
            Assert.True(body.Grounded);
            Assert.Equal(4.75f, body.Position.Y);
            steering = move.Tick(body, tuning, Behind, 0.01f, false, Dt, context);
            Assert.Equal(2, planner.Calls);
            if (steering.Status == RangeMoveStatus.InRange) return;
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            Assert.True(steering.WorldDirection.X < 0);
        }
        Assert.Fail("Grounded preparation never returned to the latest goal on the ledge.");
    }

    static MoveState PlayerStep(MoveState body, RangeSteering steering, GroundMoveContext context, PlayerPathInput input)
    {
        var frame = context.NavigationFrame;
        var binding = context.ResolvedBinding;
        Assert.Equal(MovementAvailability.Known, binding.TryAcquire(binding.CreateOrdinaryScope(frame), out var queries));
        using (queries)
        {
            var result = binding.Step(new(body with { Position = body.Position - frame.PhysicsOrigin }, frame, null),
                input.Command(steering, false), Dt, queries!);
            Assert.Equal(MovementStepOutcome.Advanced, result.Outcome);
            return result.State.State with { Position = result.State.State.Position + frame.PhysicsOrigin };
        }
    }

    static (MoveToRange Move, PathFollower Follower, Recorder Planner) Driver(GroundNavigation navigation)
    {
        var move = new MoveToRange(navigation, new PathFollowConfig
        {
            ReplanCooldownSeconds = 30, GoalRetargetTolerance = 0.1f,
        });
        var follower = Assert.IsType<PathFollower>(typeof(MoveToRange).GetField("_follower",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(move));
        FieldInfo field = typeof(PathFollower).GetField("_planner", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var planner = new Recorder(Assert.IsAssignableFrom<IRegionPathPlanner>(field.GetValue(follower)));
        field.SetValue(follower, planner);
        return (move, follower, planner);
    }

    sealed class Recorder(IRegionPathPlanner inner) : IRegionPathPlanner
    {
        internal int Calls { get; private set; }
        internal Vector3 LastStart { get; private set; }
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
