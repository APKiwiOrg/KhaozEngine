using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class ContactRebasedTerminalTests
{
    const float Dt = 1f / 30f;

    [Fact]
    public void RebasedMotionRefreshesAReachedTerminalWithoutBitEquality()
    {
        foreach (float startX in new[] { 20.475f, MathF.BitDecrement(20.475f), MathF.BitIncrement(20.475f) })
            Drive(startX);
    }

    static void Drive(float startX)
    {
        MoveTuning tuning = OrdinaryContactScene.DefaultTuning with
        {
            WalkSpeed = 3f, RunSpeed = 5f, CapsuleRadius = 0.3f, StepHeight = 0.4f,
            MaxSlopeRadians = MathF.PI / 4f, Gravity = 25f,
        };
        var boundary = MovementBoundary.Rectangle(16f, -48f, 32f, -10f);
        using var scene = new OrdinaryContactScene(tuning,
            extent: new(new(16f, -4f, -48f), new(32f, 4f, -10f), 0f, 0f),
            origin: new(128f, 0f, 0f), boundary: boundary,
            install: (world, builder) => ContactNavigationFixture.Box(world, builder, "floor",
                new(16f, -0.25f, -48f), new(32f, 0f, -10f)));
        using var context = new GroundMoveContext(scene.Height, null, scene.Physics, null, null, null, null,
            scene.Registration, boundary);
        var grid = NavGrid.FromSurfaces(64, 160, 0.25f, MathF.BitIncrement(16f), -48f,
            (_, _) => new NavSurfaceSample(true, 0f, 4f), tuning.StepHeight, 2f * tuning.CapsuleHalfHeight);
        NavSpace space = NavSpace.Single(grid);
        Vector2 endpoint = grid.CellCenter(18, 71);
        Assert.Equal(MathF.BitIncrement(20.625f), endpoint.X);
        Assert.Equal(-30.125f, endpoint.Y);
        Assert.Equal(new Vector3(128f, 0f, 0f), scene.Physics.Origin);
        var planner = new Recorder(new GridPathPlanner(space));
        var mover = new MoveToRange(planner, space, (_, _) => true, null, new()
        {
            CarryThroughStraightRuns = true, StraightenRoutes = true, Stall = new(15, 0.1f),
        });
        var body = new MoveState { Position = new(startX, 0.75f, -30.125f), Grounded = true, SpeedScale = 1f };
        float leaderX = MathF.BitIncrement(22.625f);

        Step();
        leaderX += 1f;
        Step();

        // Both positions are movement outputs. The local frame cannot represent the shifted cell centre.
        Assert.Equal(20.625f, body.Position.X);
        Assert.NotEqual(endpoint.X, body.Position.X);
        Assert.Equal(1, planner.Calls);
        leaderX += tuning.RunSpeed * Dt;
        Step();
        Assert.Equal(2, planner.Calls);
        Assert.True(body.Position.X > 20.625f, $"The reached old endpoint held the body at {body.Position}.");
        for (int tick = 3; tick < 18; tick++)
        {
            leaderX += tuning.RunSpeed * Dt;
            Step();
        }
        Assert.True(body.Position.X > 22f, $"The follower did not resume useful travel: {body.Position}.");

        void Step()
        {
            var target = ReachTarget.Capsule(new(new(leaderX, 0.75f, -30.125f), 0.3f, 0.75f));
            RangeSteering steering = mover.Tick(body, tuning, target, 1.5f, true, Dt, context);
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            body = NpcGroundMovement.Step(body, steering, true, Dt, tuning, context);
            Assert.True(body.Grounded);
        }
    }

    sealed class Recorder(IRegionPathPlanner inner) : IRegionPathPlanner
    {
        public int Calls { get; private set; }
        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float radius, PathQueryBudget budget)
        {
            Calls++;
            return inner.FindPath(start, goal, radius, budget);
        }
        public NavPath FindPath(Vector3 start, Vector3 goal, float radius, PathQueryBudget budget)
            => throw new InvalidOperationException("Range steering must use region queries.");
    }
}
