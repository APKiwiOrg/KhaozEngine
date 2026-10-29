using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactMovingGoalTests
{
    const float Dt = 1f / 30f;

    [Fact]
    public void MatchedPaceFollowKeepsMovingAcrossReplanCadences()
    {
        MoveTuning tuning = OrdinaryContactScene.DefaultTuning with
        {
            WalkSpeed = 3f, RunSpeed = 5f, CapsuleRadius = 0.3f, StepHeight = 0.4f,
            MaxSlopeRadians = MathF.PI / 4f, Gravity = 25f,
        };
        var minimum = new Vector3(16f, -4f, -48f);
        var maximum = new Vector3(25f, 4f, -10f);
        using var scene = new OrdinaryContactScene(tuning, extent: new(minimum, maximum, 0, 0),
            install: (world, builder) => ContactNavigationFixture.Box(world, builder, "floor",
                new(16f, -0.2f, -48f), new(25f, 0f, -10f)));
        using var context = new GroundMoveContext(scene.Height, null, scene.Physics, null, null, null, null,
            scene.Registration, MovementBoundary.Rectangle(16f, -48f, 25f, -10f));
        var options = new PhysicsNavBakeOptions(19.5f, -46f, 21.5f, -12f, 0.25f, 3f, 7f,
            tuning.MaxSlopeRadians, 2048, 2048);
        using var capture = PhysicsNavBake.Capture(context, options, _ => 0u);
        GroundNavigation navigation = capture.BuildProfile(tuning, default);

        // Sweep both sides of the start phase and start the leader before, at and after a 15-tick cadence.
        foreach (float startZ in new[] { -14f, MathF.BitDecrement(-14f), MathF.BitIncrement(-14f) })
            foreach (int leaderStarts in new[] { 3, 14, 15, 16 })
                Follow(startZ, leaderStarts, tuning, navigation, context);
    }

    static void Follow(float startZ, int leaderStarts, MoveTuning tuning,
        GroundNavigation navigation, GroundMoveContext context)
    {
        var recorder = new RecordingPlanner(navigation.Planner);
        var mover = new MoveToRange(recorder, navigation.Space, navigation.AllowsSegment, null, new()
        {
            CarryThroughStraightRuns = true,
            StraightenRoutes = true,
            Stall = new(15, 0.1f),
        });
        var body = new MoveState { Position = new(20.5f, tuning.CapsuleHalfHeight, startZ), Grounded = true, SpeedScale = 1f };
        var leader = body with { Position = body.Position - new Vector3(0f, 0f, 3f) };
        string? blocked = null, selfWaypoint = null;
        for (int tick = 0; tick < leaderStarts + 150; tick++)
        {
            if (tick >= leaderStarts)
                leader = NpcGroundMovement.Step(leader, new(-Vector2.UnitY, RangeMoveStatus.Following), true, Dt, tuning, context);
            var target = ReachTarget.Capsule(new(leader.Position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight));
            RangeSteering steering = mover.Tick(body, tuning, target, 1.5f, true, Dt, context);
            if (steering.Status == RangeMoveStatus.Blocked)
                blocked ??= $"Blocked at tick {tick}, body {body.Position}, leader {leader.Position}, last plan {recorder.Describe()}";
            Assert.Contains(steering.Status, new[] { RangeMoveStatus.Following, RangeMoveStatus.InRange, RangeMoveStatus.Blocked });
            NavPath? active = mover.Straightener?.LastStraightened ?? recorder.Last;
            if (active is { Status: NavPathStatus.Complete, Waypoints.Count: 1 } path &&
                path.Waypoints[0].Position == new Vector2(body.Position.X, body.Position.Z) &&
                !ReachGeometry.Within(new(body.Position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight), target, 1.5f))
                selfWaypoint ??= $"Self waypoint at tick {tick}, body {body.Position}, leader {leader.Position}, last plan {recorder.Describe()}";
            Assert.True(navigation.AllowsSegment(body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight,
                leader.Position - Vector3.UnitY * tuning.CapsuleHalfHeight));
            body = NpcGroundMovement.Step(body, steering, true, Dt, tuning, context);
            Assert.True(body.Grounded);
        }
        Assert.True(blocked is null && selfWaypoint is null,
            $"Start {startZ:R}, leader cadence {leaderStarts}: {blocked} | {selfWaypoint}");
        Assert.True(body.Position.Z < -37f, $"Follower stopped at {body.Position}.");
        Assert.True(recorder.Calls > 5);
    }

    sealed class RecordingPlanner(IRegionPathPlanner inner) : IRegionPathPlanner
    {
        public NavPath? Last { get; private set; }
        public int Calls { get; private set; }
        Vector3 _start;
        Vector3 _goal;
        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)
        {
            _start = start;
            _goal = goal.Anchor;
            Calls++;
            return Last = inner.FindPath(start, goal, agentRadius, budget);
        }
        public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget)
            => throw new InvalidOperationException("Range steering must use region queries.");
        public string Describe() => $"query {Calls}, start {_start}, goal {_goal}, {Last?.Status}, waypoints {Last?.Waypoints.Count}";
    }
}
