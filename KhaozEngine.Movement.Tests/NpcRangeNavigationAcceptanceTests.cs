using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class NpcRangeNavigationAcceptanceTests
{
    private const float Dt = 1f / 30f;
    private static readonly MoveTuning Tuning = MoveTuning.Default with
    {
        WalkSpeed = 2f,
        RunSpeed = 5f,
        CapsuleRadius = 0.2f,
        CapsuleHalfHeight = 0.75f,
        StepHeight = 0.4f,
        MaxSlopeRadians = 0.8f,
    };
    private static readonly PhysicsNavBakeOptions Options = new(-3.25f, -2.75f, 3.25f, 2.75f,
        0.5f, 5f, 6f, 0.8f, 256, 1024);

    [Theory]
    [InlineData(false, 1f)]
    [InlineData(true, 2f)]
    public void CapsuleApproachReachesTheActualStopRingWithoutOvershoot(bool run, float scale)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        using var bake = PhysicsNavBake.Capture(context, Options, _ => 0u);
        var move = new MoveToRange(bake.BuildProfile(Tuning, default));
        var target = ReachTarget.Capsule(new MovementBody(new Vector3(2f, 0.75f, 0f), 0.2f, 0.75f));
        MoveState body = Standing(-2f, 0f, Tuning);
        body.SpeedScale = scale;

        body = Approach(move, body, Tuning, target, 0.6f, run, context, world);

        AssertAtRing(body, Tuning, target, 0.6f);
        Assert.InRange(body.Position.X, 0.999f, 1.001f);
        Vector3 arrived = body.Position;
        RangeSteering held = move.Tick(body, Tuning, target, 0.6f, run, Dt, context);
        Assert.Equal(RangeMoveStatus.InRange, held.Status);
        body = NpcGroundMovement.Step(body, held, run, Dt, Tuning, context);
        Assert.Equal(arrived.X, body.Position.X);
        Assert.Equal(arrived.Z, body.Position.Z);
        AssertAtRing(body, Tuning, target, 0.6f);
    }

    [Theory]
    [InlineData(0.2f)]
    [InlineData(0.55f)]
    public void RealPhysicsDetourCarriesTheActualCapsuleAroundTheWall(float radius)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        Vector3 wallCentre = new(0f, 1f, 0f), wallExtents = new(0.1f, 1f, 1f);
        world.AddStatic(new BoxShape(wallExtents), Pose.At(wallCentre));
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        MoveTuning tuning = Tuning with { CapsuleRadius = radius };
        using var bake = PhysicsNavBake.Capture(context, Options, _ => 0u);
        var move = new MoveToRange(bake.BuildProfile(tuning, default));
        var target = ReachTarget.Capsule(new MovementBody(new Vector3(2f, 0.75f, 0f), 0.2f, 0.75f));
        MoveState body = Standing(-2f, 0f, tuning);
        body.SpeedScale = 2f;
        float largestDetour = 0f;

        body = Approach(move, body, tuning, target, 0.6f, true, context, world, state =>
        {
            largestDetour = MathF.Max(largestDetour, MathF.Abs(state.Position.Z));
            AssertObstacleClearance(state, tuning, wallCentre, wallExtents);
        });

        Assert.True(largestDetour >= 1f + radius);
        AssertAtRing(body, tuning, target, 0.6f);
        Assert.True(body.Position.X > 0f);
    }

    [Fact]
    public void SolidFootprintBoxApproachStopsAtActualInteractionRange()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        Vector3 centre = new(2f, 1f, 0f), extents = new(0.75f, 1f, 0.5f);
        world.AddStatic(new BoxShape(extents), Pose.At(centre));
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        using var bake = PhysicsNavBake.Capture(context, Options, _ => 0u);
        var move = new MoveToRange(bake.BuildProfile(Tuning, default));
        var target = ReachTarget.Box(centre, extents);
        MoveState body = Standing(-2f, 0f, Tuning);
        body.SpeedScale = 2f;

        body = Approach(move, body, Tuning, target, 1.5f, true, context, world,
            state => AssertObstacleClearance(state, Tuning, centre, extents));

        AssertAtRing(body, Tuning, target, 1.5f);
        Assert.InRange(body.Position.X, -0.451f, -0.449f);
    }

    [Theory]
    [InlineData(0.2f)]
    [InlineData(0.6f)]
    public void UnreachableRangeCannotPullAnyPartOfTheBodyOutsideItsPen(float radius)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        MoveTuning tuning = Tuning with { CapsuleRadius = radius };
        using var bake = PhysicsNavBake.Capture(context, Options, feet => feet.X < 1f ? 0x01u : 0x02u);
        GroundNavigation pen = bake.BuildProfile(tuning, new NavAreaFilter(0x01u, 0x02u));
        var move = new MoveToRange(pen, new PathFollowConfig { ReplanCooldownSeconds = 10f });
        var target = ReachTarget.Capsule(new MovementBody(new Vector3(2f, 0.75f, 0f), 0.2f, 0.75f));
        MoveState body = Standing(-1f, 0f, tuning);
        bool refused = false;

        for (int tick = 0; tick < 90; tick++)
        {
            RangeSteering steering = move.Tick(body, tuning, target, 0.6f, true, Dt, context);
            refused |= steering.Status is RangeMoveStatus.Unreachable or RangeMoveStatus.WaitingForPath;
            Assert.NotEqual(RangeMoveStatus.InRange, steering.Status);
            body = NpcGroundMovement.Step(body, steering, true, Dt, tuning, context);
            Assert.True(body.Position.X + radius <= 1f);
            Assert.True(pen.AllowsSegment(Feet(body, tuning), Feet(body, tuning)));
            AssertPhysicsClear(world, body, tuning);
        }

        Assert.True(refused);
        Assert.False(ReachGeometry.Within(Shape(body, tuning), target, 0.6f));
    }

    [Fact]
    public void ResetAfterTeleportPlansFromTheNewBodyInsideTheOldCooldown()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        using var bake = PhysicsNavBake.Capture(context, Options, _ => 0u);
        var move = new MoveToRange(bake.BuildProfile(Tuning, default), new PathFollowConfig
        {
            ReplanCooldownSeconds = 60f,
            CorridorTolerance = 0.25f,
        });
        var target = ReachTarget.Capsule(new MovementBody(new Vector3(2f, 0.75f, 0f), 0.2f, 0.75f));
        MoveState body = Standing(-2f, 0f, Tuning);
        RangeSteering initial = move.Tick(body, Tuning, target, 0.6f, false, Dt, context);
        Assert.Equal(RangeMoveStatus.Following, initial.Status);
        body = NpcGroundMovement.Step(body, initial, false, Dt, Tuning, context);
        body.Position = new Vector3(2f, 0.75f, 2f);
        move.Reset();

        RangeSteering reset = move.Tick(body, Tuning, target, 0.6f, false, Dt, context);

        Assert.Equal(RangeMoveStatus.Following, reset.Status);
        Assert.True(reset.WorldDirection.Y < 0f);
        body = Approach(move, body, Tuning, target, 0.6f, false, context, world);
        AssertAtRing(body, Tuning, target, 0.6f);
        Assert.InRange(body.Position.Z, 0.999f, 1.001f);
    }

    private static MoveState Approach(MoveToRange move, MoveState body, MoveTuning tuning,
        ReachTarget target, float range, bool run, GroundMoveContext context, BepuPhysicsWorld world,
        Action<MoveState>? inspect = null)
    {
        for (int tick = 0; tick < 300; tick++)
        {
            RangeSteering steering = move.Tick(body, tuning, target, range, run, Dt, context);
            if (steering.Status == RangeMoveStatus.InRange)
            {
                Assert.True(ReachGeometry.Within(Shape(body, tuning), target, range));
                return body;
            }
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            body = NpcGroundMovement.Step(body, steering, run, Dt, tuning, context);
            Assert.True(body.Grounded);
            AssertPhysicsClear(world, body, tuning);
            inspect?.Invoke(body);
            Assert.True(ReachGeometry.Distance(Shape(body, tuning), target) >= range - 0.001f);
            if (ReachGeometry.Within(Shape(body, tuning), target, range)) return body;
        }
        Assert.Fail($"No actual shape reach after 300 ticks at {body.Position}.");
        return body;
    }

    private static void AssertAtRing(MoveState body, MoveTuning tuning, ReachTarget target, float range)
    {
        MovementBody actual = Shape(body, tuning);
        Assert.True(ReachGeometry.Within(actual, target, range));
        Assert.InRange(ReachGeometry.Distance(actual, target), range - 0.001f, range);
    }

    private static void AssertPhysicsClear(BepuPhysicsWorld world, MoveState body, MoveTuning tuning)
    {
        bool penetrates = world.ComputePenetration(CharacterMovement.CapsuleFor(tuning),
            Pose.At(body.Position - world.Origin), out Vector3 mtv);
        if (!penetrates) return;
        // Native floor tangency may report a tiny vertical MTV. Lateral overlap is never accepted.
        Assert.Equal(0f, mtv.X);
        Assert.Equal(0f, mtv.Z);
        Assert.InRange(MathF.Abs(mtv.Y), 0f, 0.000001f);
    }

    private static void AssertObstacleClearance(MoveState body, MoveTuning tuning, Vector3 centre, Vector3 extents)
    {
        double dx = body.Position.X - Math.Clamp(body.Position.X, centre.X - extents.X, centre.X + extents.X);
        double dz = body.Position.Z - Math.Clamp(body.Position.Z, centre.Z - extents.Z, centre.Z + extents.Z);
        Assert.True(dx * dx + dz * dz >= (double)tuning.CapsuleRadius * tuning.CapsuleRadius);
    }

    private static MoveState Standing(float x, float z, MoveTuning tuning)
        => new() { Position = new Vector3(x, tuning.CapsuleHalfHeight, z), Grounded = true };

    private static Vector3 Feet(MoveState body, MoveTuning tuning)
        => body.Position - Vector3.UnitY * tuning.CapsuleHalfHeight;

    private static MovementBody Shape(MoveState body, MoveTuning tuning)
        => new(body.Position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight);
}
