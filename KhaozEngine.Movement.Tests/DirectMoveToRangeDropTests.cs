using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class DirectMoveToRangeDropTests
{
    private const float Dt = 1f / 30f;
    private const float LedgeTop = 2f;
    private static readonly DirectApproachOptions Strict = new(15, 0.1f, 45, 0.1f);
    private static readonly MoveTuning Tuning = MoveToRangeTests.Tuning;
    private static readonly ReachTarget Beyond = ReachTarget.Point(new(4f, 0.75f, 0f));

    [Fact]
    public void DropWithinTheAllowanceArrivesAfterOneSuspendedStretch()
    {
        using var world = LedgeWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        Walk walk = Approach(Strict with { MaxDropMetres = 2.5f }, context, OnLedge(), Beyond);

        Assert.Equal(RangeMoveStatus.InRange, walk.Last);
        Assert.Equal(1, walk.SuspendedStretches);
        Assert.Equal(0, walk.CountedWhileAirborne);
        Assert.Equal(0, walk.Blocked);
        Assert.InRange(walk.Body.Position.Y, 0.74f, 0.76f);
        Assert.True(ReachGeometry.Within(MoveToRangeTests.Shape(walk.Body, Tuning), Beyond, 0.5f));
    }

    [Fact]
    public void LedgeDeeperThanTheAllowanceLatchesBlocked()
    {
        using var world = LedgeWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        Walk walk = Approach(Strict with { MaxDropMetres = 1.5f }, context, OnLedge(), Beyond);

        Assert.Equal(RangeMoveStatus.Blocked, walk.Last);
        Assert.Equal(0, walk.SuspendedStretches);
        Assert.True(walk.AlwaysGrounded);
        Assert.InRange(walk.Body.Position.X, 0.5f, 1.2f);
        Assert.True(walk.Body.Position.Y > LedgeTop);
    }

    [Fact]
    public void DefaultOptionsKeepRefusingTheSameLedge()
    {
        Assert.Equal(0f, Strict.MaxDropMetres);
        using var world = LedgeWorld();
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        Walk walk = Approach(Strict, context, OnLedge(), Beyond);

        Assert.Equal(RangeMoveStatus.Blocked, walk.Last);
        Assert.Equal(0, walk.SuspendedStretches);
        Assert.True(walk.AlwaysGrounded);
        Assert.InRange(walk.Body.Position.X, 0.5f, 1.2f);
        Assert.True(walk.Body.Position.Y > LedgeTop);
    }

    [Fact]
    public void DropIntoSwimDepthWaterIsRefused()
    {
        // A terrain ledge from 1 m down to -1 m. The water surface at 0.5 m is 1.5 m deep below the ledge.
        static float Height(float x, float z) => x < 1f ? 1f : -1f;
        DirectApproachOptions options = Strict with { MaxDropMetres = 3f };
        var start = new MoveState { Position = new Vector3(-1f, 1.75f, 0f), Grounded = true };
        ReachTarget below = ReachTarget.Point(new(4f, -0.25f, 0f));

        Walk dry = Approach(options, new GroundMoveContext(Height), start, below);
        Assert.Equal(RangeMoveStatus.InRange, dry.Last);
        Assert.Equal(1, dry.SuspendedStretches);

        var wet = new GroundMoveContext(Height, medium: (_, _, feetY) => new MovementMedium(0.5f, feetY < 0.5f));
        Walk refused = Approach(options, wet, start, below);
        Assert.Equal(RangeMoveStatus.Blocked, refused.Last);
        Assert.Equal(0, refused.SuspendedStretches);
        Assert.True(refused.AlwaysGrounded);
        Assert.False(refused.Body.Swimming);
        Assert.True(refused.Body.Position.Y > 1f);
    }

    [Theory]
    [InlineData(-0.001f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void DropAllowanceMustBeFiniteAndNotNegative(float metres)
    {
        var refused = Assert.Throws<ArgumentOutOfRangeException>(() => Strict with { MaxDropMetres = metres });
        Assert.Equal(nameof(DirectApproachOptions.MaxDropMetres), refused.ParamName);
        Assert.Equal(1.5f, (Strict with { MaxDropMetres = 1.5f }).MaxDropMetres);
    }

    // A 4 by 2 by 4 m box with its top at 2 m and its east edge at x = 1 on a flat floor.
    internal static BepuPhysicsWorld LedgeWorld()
    {
        BepuPhysicsWorld world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, 1f, 2f)), Pose.At(new Vector3(-1f, 1f, 0f)));
        return world;
    }

    private static MoveState OnLedge() => new() { Position = new Vector3(-1f, LedgeTop + 0.75f, 0f), Grounded = true };

    private static Walk Approach(DirectApproachOptions options, GroundMoveContext context, MoveState body,
        in ReachTarget target)
    {
        var driver = new DirectMoveToRange(options);
        var walk = new Walk { AlwaysGrounded = true };
        bool wasSuspended = false;
        for (int tick = 0; tick < 300; tick++)
        {
            RangeSteering steering = driver.Tick(body, Tuning, target, 0.5f, false, false, Dt, context);
            walk.Last = steering.Status;
            bool suspended = steering.Status == RangeMoveStatus.Suspended;
            if (suspended && !wasSuspended) walk.SuspendedStretches++;
            wasSuspended = suspended;
            if (!body.Grounded && !suspended) walk.CountedWhileAirborne++;
            if (steering.Status == RangeMoveStatus.Blocked) walk.Blocked++;
            if (steering.Status is RangeMoveStatus.InRange or RangeMoveStatus.Blocked) break;
            body = NpcGroundMovement.Step(body, steering, false, Dt, Tuning, context);
            walk.AlwaysGrounded &= body.Grounded;
        }
        walk.Body = body;
        return walk;
    }

    private sealed class Walk
    {
        public MoveState Body;
        public RangeMoveStatus Last;
        public int SuspendedStretches;
        public int CountedWhileAirborne;
        public int Blocked;
        public bool AlwaysGrounded;
    }
}
