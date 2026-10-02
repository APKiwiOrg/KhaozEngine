using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Movement.MoveToRangeTests;

namespace KhaozEngine.Tests.Movement;

public class MoveToRangeAreaTests
{
    [Theory]
    [InlineData(1f / 30f, 1f)]
    [InlineData(0.1f, 1f)]
    [InlineData(1f / 30f, 3f)]
    [InlineData(0.1f, 3f)]
    public void FastScaledApproachCapsTheWaypointAndRangeTravel(float dt, float mediumScale)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new(0.025f, 1f, 0.7f)), Pose.At(new(0f, 1f, -0.5f)));
        MoveTuning tuning = Tuning with { CapsuleRadius = 0.04f, CapsuleHalfHeight = 0.2f };
        var context = new GroundMoveContext((_, _) => 0f, physics: world,
            medium: (_, _, _) => new MovementMedium(0.01f, true, mediumScale));
        using var bake = PhysicsNavBake.Capture(context, new(-0.875f, -1.375f, 0.875f, 0.875f,
            0.25f, 4f, 5f, 0.8f, 128, 512), _ => 0u);
        GroundNavigation nav = bake.BuildProfile(tuning, default);
        var mover = new MoveToRange(nav);
        MoveState body = new() { Position = new(-0.25f, 0.2f, 0f), Grounded = true, SpeedScale = 2f };
        ReachTarget target = ReachTarget.Point(new(0.25f, 0.2f, -0.5f));
        bool crossedWall = false;
        bool reachedTurn = false;
        for (int tick = 0; tick < 64; tick++)
        {
            RangeSteering steering = mover.Tick(body, tuning, target, 0.06f, true, dt, context);
            if (steering.Status == RangeMoveStatus.InRange) break;
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            MoveState next = context.Step(body, steering.WorldDirection, true, dt, tuning);
            Assert.True(nav.AllowsSegment(Feet(body, tuning), Feet(next, tuning)));
            if (body.Position.X < -0.1f && next.Position.X > -0.1f)
                Assert.True(body.Position.Z >= 0.2499f);
            reachedTurn |= next.Position.Z >= 0.2499f;
            crossedWall |= next.Position.X > -0.065f && next.Position.X < 0.065f && next.Position.Z < 0.24f;
            Assert.False(world.ComputePenetration(new CapsuleShape(0.04f, 0.32f), Pose.At(next.Position), out _));
            body = next;
        }
        Assert.True(reachedTurn);
        Assert.False(crossedWall);
        Assert.True(ReachGeometry.Within(Shape(body, tuning), target, 0.06f));
        Assert.InRange(ReachGeometry.Distance(Shape(body, tuning), target), 0.05999f, 0.06f);
        Assert.Equal(RangeMoveStatus.InRange, mover.Tick(body, tuning, target, 0.06f, true, dt, context).Status);
    }

    [Theory]
    [InlineData(NavPathStatus.Unreachable)]
    [InlineData(NavPathStatus.Partial)]
    public void EveryFallbackKeepsTheAreaGuard(NavPathStatus status)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        using var bake = PhysicsNavBake.Capture(context, new(-1.125f, -0.625f, 1.125f, 0.625f,
            0.25f, 4f, 5f, 0.8f, 128, 512), feet => feet.X < 0.75f ? 1u : 0u);
        GroundNavigation pen = bake.BuildProfile(Tuning, new NavAreaFilter(1u, 0u));
        var planner = new ScriptPlanner((_, _) => status == NavPathStatus.Unreachable ? NavPath.Unreachable : Route(status, new Vector2(0.25f, 0f)));
        var mover = new MoveToRange(planner, pen.Space, pen.AllowsSegment);
        MoveState body = Body();
        ReachTarget target = ReachTarget.Point(new(0.9f, 0.75f, 0f));
        bool areaWasEscaped = false;
        bool waited = false;
        for (int tick = 0; tick < 12; tick++)
        {
            RangeSteering steering = mover.Tick(body, Tuning, target, 0.1f, true, 1f / 30f, context);
            if (steering.Status != RangeMoveStatus.Following) Assert.Equal(Vector2.Zero, steering.WorldDirection);
            waited |= steering.Status == RangeMoveStatus.WaitingForPath;
            MoveState next = context.Step(body, steering.WorldDirection, true, 1f / 30f, Tuning);
            Assert.True(pen.AllowsSegment(Feet(body, Tuning), Feet(next, Tuning)));
            areaWasEscaped |= next.Position.X > 0.25f;
            body = next;
        }
        Assert.False(areaWasEscaped);
        Assert.Equal(status == NavPathStatus.Partial, waited);
        Assert.False(ReachGeometry.Within(Shape(body, Tuning), target, 0.1f));
        Assert.Equal(1, planner.Queries);
    }

    [Fact]
    public void BlockedNearFieldShortcutRetainsThePlannedDetour()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new(0.025f, 1f, 0.4f)), Pose.At(new(0.5f, 1f, 0f)));
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        var planner = new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f)));
        var mover = new MoveToRange(planner, Space, (_, _) => true);
        ReachTarget target = ReachTarget.Point(new(1f, 0.75f, 0f));
        RangeSteering steering = mover.Tick(Body(), Tuning, target, 0.1f, true, 0.2f, context);
        Assert.Equal(RangeMoveStatus.Following, steering.Status);
        Assert.Equal(0f, steering.WorldDirection.X);
        Assert.True(steering.WorldDirection.Y > 0f);
        MoveState next = context.Step(Body(), steering.WorldDirection, true, 0.2f, Tuning);
        Assert.Equal(0f, next.Position.X, 5);
        Assert.InRange(next.Position.Z, 0.8f, 1f);
    }

    [Fact]
    public void SolidBoxCanBeReachedAtTheFarFaceThroughARealProfile()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new(0.4f, 0.75f, 0.6f)), Pose.At(new(0f, 0.75f, 0f)));
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        using var bake = PhysicsNavBake.Capture(context, new(-1.625f, -1.625f, 1.625f, 1.625f,
            0.25f, 4f, 5f, 0.8f, 256, 1024), feet => feet.X >= 0.5f ? 1u : 0u);
        GroundNavigation nav = bake.BuildProfile(Tuning, new NavAreaFilter(1u, 0u));
        var mover = new MoveToRange(nav);
        ReachTarget target = ReachTarget.Box(new(0f, 0.75f, 0f), new(0.4f, 0.75f, 0.6f));
        MoveState body = Body(1.25f);
        for (int tick = 0; tick < 24; tick++)
        {
            RangeSteering steering = mover.Tick(body, Tuning, target, 0.3f, false, 1f / 30f, context);
            if (steering.Status == RangeMoveStatus.InRange) break;
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            MoveState next = context.Step(body, steering.WorldDirection, false, 1f / 30f, Tuning);
            Assert.True(nav.AllowsSegment(Feet(body, Tuning), Feet(next, Tuning)));
            body = next;
        }
        Assert.True(ReachGeometry.Within(Shape(body, Tuning), target, 0.3f));
        Assert.InRange(body.Position.X, 0.89999f, 0.9f);
        Assert.True(body.Position.X - Tuning.CapsuleRadius > 0.4f);
        if (world.ComputePenetration(new CapsuleShape(0.2f, 1.1f), Pose.At(body.Position), out Vector3 penetration))
        {
            Assert.Equal(0f, penetration.X);
            Assert.Equal(0f, penetration.Z);
            Assert.True(MathF.Abs(penetration.Y) <= 0.000001f, $"Native floor contact MTV: {penetration}");
        }
    }

    [Fact]
    public void NoRegionMemberHoldsEvenIfTheBoundingCircleOverlapsTheBody()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        using var bake = PhysicsNavBake.Capture(context, new(-1.125f, -1.125f, 1.125f, 1.125f,
            0.25f, 4f, 5f, 0.8f, 128, 512), _ => 0u);
        GroundNavigation nav = bake.BuildProfile(Tuning, default);
        var mover = new MoveToRange(nav);
        ReachTarget target = ReachTarget.Box(new(0f, 4f, 0f), new(0.8f, 0.1f, 0.8f));
        RangeSteering steering = mover.Tick(Body(), Tuning, target, 0.1f, true, 0.1f, context);
        Assert.Equal(RangeMoveStatus.Unreachable, steering.Status);
        Assert.Equal(Vector2.Zero, steering.WorldDirection);
        MoveState moved = context.Step(Body(), steering.WorldDirection, true, 0.1f, Tuning);
        Assert.InRange(Vector3.Distance(Body().Position, moved.Position), 0f, 0.00001f);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void HighLevelDriverChecksAllFourProfileGeometryFields(int field)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var bake = PhysicsNavBake.Capture(new GroundMoveContext((_, _) => 0f, physics: world),
            new(-0.5f, -0.5f, 0.5f, 0.5f, 0.5f, 4f, 5f, 0.8f, 32, 128), _ => 0u);
        var mover = new MoveToRange(bake.BuildProfile(Tuning, default));
        MoveTuning wrong = field switch
        {
            0 => Tuning with { CapsuleRadius = 0.21f },
            1 => Tuning with { CapsuleHalfHeight = 0.8f },
            2 => Tuning with { MaxSlopeRadians = 0.7f },
            _ => Tuning with { StepHeight = 0.3f },
        };
        Assert.Throws<ArgumentException>(() => { mover.Tick(Body(), wrong, ReachTarget.Point(Vector3.Zero), 0f, false, 0.1f, Flat); });
    }
}
