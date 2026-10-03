using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Movement;

[Collection("AllocSensitive")]
public class DirectMoveToRangeTests
{
    private const float Dt = 1f / 30f;
    private static readonly DirectApproachOptions Options = new(15, 0.1f, 45, 0.1f);
    private static readonly MoveTuning Tuning = MoveToRangeTests.Tuning;
    private static readonly GroundMoveContext Flat = new((_, _) => 0f);
    private static readonly ReachTarget Far = ReachTarget.Point(new(50f, 0.75f, 0f));

    [Fact]
    public void StallLatchesOnTheSixteenthCountedTick()
    {
        var driver = new DirectMoveToRange(Options);
        for (int tick = 1; tick <= 30; tick++)
        {
            RangeSteering steering = Tick(driver, MoveToRangeTests.Body(0.005f * tick), Far);
            if (tick < 16)
            {
                Assert.Equal(RangeMoveStatus.Following, steering.Status);
                Assert.True(steering.WorldDirection.X > 0f);
            }
            else
            {
                Assert.Equal(RangeMoveStatus.Blocked, steering.Status);
                Assert.Equal(Vector2.Zero, steering.WorldDirection);
            }
        }
        driver.Reset();
        Assert.Equal(RangeMoveStatus.Following, Tick(driver, MoveToRangeTests.Body(0.15f), Far).Status);
    }

    [Fact]
    public void ApproachLatchesOnTheFortySixthCountedTick()
    {
        ReachTarget centre = ReachTarget.Point(new(0f, 0.75f, 0f));
        var still = new DirectMoveToRange(Options);
        for (int tick = 1; tick <= 46; tick++)
        {
            RangeSteering steering = Tick(still, Orbit(tick), centre);
            Assert.Equal(tick < 46 ? RangeMoveStatus.Following : RangeMoveStatus.Blocked, steering.Status);
        }

        var moving = new DirectMoveToRange(Options);
        for (int tick = 1; tick <= 120; tick++)
            Assert.Equal(RangeMoveStatus.Following, Tick(moving, Orbit(tick), centre, targetMoves: true).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuspendedTicksCountTowardNeitherWindow(bool committed)
    {
        var driver = new DirectMoveToRange(Options);
        for (int tick = 1; tick <= 15; tick++)
            Assert.Equal(RangeMoveStatus.Following, Tick(driver, MoveToRangeTests.Body(), Far).Status);
        for (int tick = 0; tick < 10; tick++)
        {
            // Suspended samples far from the stalled spot would break the stall if they were recorded.
            RangeSteering suspended = Tick(driver, Suspended(1f + 0.5f * tick, committed), Far);
            Assert.Equal(RangeMoveStatus.Suspended, suspended.Status);
            Assert.Equal(Vector2.Zero, suspended.WorldDirection);
        }
        Assert.Equal(RangeMoveStatus.Blocked, Tick(driver, MoveToRangeTests.Body(), Far).Status);
    }

    [Fact]
    public void LatchedBlockSurvivesSuspendedTicks()
    {
        var driver = new DirectMoveToRange(Options);
        AssertStallLatchesAfter(driver, MoveToRangeTests.Body(), Far, 0.5f, Tuning, 16);
        for (int tick = 0; tick < 5; tick++)
            Assert.Equal(RangeMoveStatus.Suspended, Tick(driver, Suspended(1f + 0.5f * tick, false), Far).Status);
        RangeSteering grounded = Tick(driver, MoveToRangeTests.Body(4f), Far);
        Assert.Equal(RangeMoveStatus.Blocked, grounded.Status);
        Assert.Equal(Vector2.Zero, grounded.WorldDirection);
    }

    [Fact]
    public void RootedBodyHoldsFollowingAndIsNeverBlocked()
    {
        var driver = new DirectMoveToRange(Options);
        MoveState rooted = MoveToRangeTests.Body();
        rooted.SpeedScale = 0f;
        for (int tick = 0; tick < 60; tick++)
        {
            RangeSteering held = Tick(driver, rooted, Far);
            Assert.Equal(RangeMoveStatus.Following, held.Status);
            Assert.Equal(Vector2.Zero, held.WorldDirection);
        }
        MoveState released = MoveToRangeTests.Body();
        for (int tick = 1; tick <= 16; tick++)
        {
            RangeSteering steering = Tick(driver, released, Far);
            Assert.Equal(tick < 16 ? RangeMoveStatus.Following : RangeMoveStatus.Blocked, steering.Status);
        }
    }

    [Fact]
    public void InRangeClearsWindowsAndLatch()
    {
        var driver = new DirectMoveToRange(Options);
        ReachTarget target = ReachTarget.Point(new(5f, 0.75f, 0f));
        AssertStallLatchesAfter(driver, MoveToRangeTests.Body(), target, 0.5f, Tuning, 16);
        Assert.Equal(RangeMoveStatus.InRange, Tick(driver, MoveToRangeTests.Body(4.6f), target).Status);
        AssertStallLatchesAfter(driver, MoveToRangeTests.Body(), target, 0.5f, Tuning, 16);
    }

    [Fact]
    public void TargetMovesToggleClearsOnlyTheApproachWindow()
    {
        ReachTarget centre = ReachTarget.Point(new(0f, 0.75f, 0f));
        var approach = new DirectMoveToRange(Options);
        for (int tick = 1; tick <= 77; tick++)
        {
            RangeSteering steering = Tick(approach, Orbit(tick), centre, targetMoves: tick == 31);
            // The toggle on tick 31 and back on tick 32 each restart the approach window.
            Assert.Equal(tick < 77 ? RangeMoveStatus.Following : RangeMoveStatus.Blocked, steering.Status);
        }

        var stall = new DirectMoveToRange(Options);
        for (int tick = 1; tick <= 16; tick++)
        {
            RangeSteering steering = Tick(stall, MoveToRangeTests.Body(), Far, targetMoves: tick > 10);
            Assert.Equal(tick < 16 ? RangeMoveStatus.Following : RangeMoveStatus.Blocked, steering.Status);
        }
    }

    [Fact]
    public void ShapeRangeAndGeometryChangesReset()
    {
        Vector3 centre = new(5f, 0.75f, 0f);
        ReachTarget box = ReachTarget.Box(centre, new(0.5f, 0.5f, 0.5f));
        ReachTarget capsule = ReachTarget.Capsule(new MovementBody(centre, 0.5f, 0.75f));
        var changes = new (ReachTarget Base, ReachTarget Target, float Range, MoveTuning Tuning)[]
        {
            (box, ReachTarget.Point(centre), 0.5f, Tuning),
            (box, capsule, 0.5f, Tuning),
            (box, ReachTarget.Box(centre, new(0.6f, 0.5f, 0.5f)), 0.5f, Tuning),
            (box, ReachTarget.Box(centre, new(0.5f, 0.5f, 0.5f), 0.1f), 0.5f, Tuning),
            (box, box, 0.6f, Tuning),
            (box, box, 0.5f, Tuning with { CapsuleRadius = 0.25f }),
            (box, box, 0.5f, Tuning with { CapsuleHalfHeight = 0.8f }),
            (box, box, 0.5f, Tuning with { StepHeight = 0.3f }),
            (box, box, 0.5f, Tuning with { MaxSlopeRadians = 0.7f }),
            (capsule, ReachTarget.Capsule(new MovementBody(centre, 0.6f, 0.75f)), 0.5f, Tuning),
            (capsule, ReachTarget.Capsule(new MovementBody(centre, 0.5f, 0.9f)), 0.5f, Tuning),
        };
        foreach (var change in changes)
        {
            var driver = new DirectMoveToRange(Options);
            AssertStallLatchesAfter(driver, MoveToRangeTests.Body(), change.Base, 0.5f, Tuning, 16);
            ReachTarget translated = Translated(change.Base, Vector3.UnitZ);
            Assert.Equal(RangeMoveStatus.Blocked, Tick(driver, MoveToRangeTests.Body(), translated).Status);
            AssertStallLatchesAfter(driver, MoveToRangeTests.Body(), change.Target, change.Range, change.Tuning, 16);
        }
    }

    [Fact]
    public void WallStallLatchesBlockedThroughThePhysicsCore()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(0.05f, 1f, 2f)), Pose.At(new Vector3(0f, 1f, 0f)));
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        var driver = new DirectMoveToRange(Options);
        ReachTarget target = ReachTarget.Point(new(3f, 0.75f, 0f));
        MoveState body = MoveToRangeTests.Body(-2f);
        var samples = new List<Vector2>();
        int expected = -1, blocked = -1;
        for (int tick = 0; tick < 300 && blocked < 0; tick++)
        {
            samples.Add(new Vector2(body.Position.X, body.Position.Z));
            int count = samples.Count;
            if (expected < 0 && count > 15 && Vector2.Distance(samples[count - 1], samples[count - 16]) < 0.1f)
                expected = count;
            RangeSteering steering = driver.Tick(body, Tuning, target, 0.5f, false, false, Dt, context);
            if (steering.Status == RangeMoveStatus.Blocked)
            {
                blocked = count;
                break;
            }
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            body = NpcGroundMovement.Step(body, steering, false, Dt, Tuning, context);
            Assert.True(body.Grounded);
        }
        Assert.True(blocked > 16);
        Assert.Equal(expected, blocked);
        Assert.True(body.Position.X < 0f);
    }

    [Fact]
    public void LedgePreflightRefusalCountsAsRequestedTravel()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, 1f, 2f)), Pose.At(new Vector3(-1f, 1f, 0f)));
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        var driver = new DirectMoveToRange(Options);
        ReachTarget target = ReachTarget.Point(new(4f, 0.75f, 0f));
        MoveState body = new() { Position = new Vector3(-1f, 2.75f, 0f), Grounded = true };
        RangeSteering steering = default;
        for (int tick = 0; tick < 120; tick++)
        {
            steering = driver.Tick(body, Tuning, target, 0.5f, false, false, Dt, context);
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            if (steering.WorldDirection == Vector2.Zero) break;
            body = NpcGroundMovement.Step(body, steering, false, Dt, Tuning, context);
            Assert.True(body.Grounded);
        }
        Assert.Equal(Vector2.Zero, steering.WorldDirection);
        Assert.InRange(body.Position.X, 0.5f, 1.2f);

        driver.Reset();
        for (int tick = 1; tick <= 16; tick++)
        {
            steering = driver.Tick(body, Tuning, target, 0.5f, false, false, Dt, context);
            Assert.Equal(tick < 16 ? RangeMoveStatus.Following : RangeMoveStatus.Blocked, steering.Status);
            Assert.Equal(Vector2.Zero, steering.WorldDirection);
            body = NpcGroundMovement.Step(body, steering, false, Dt, Tuning, context);
            Assert.True(body.Grounded);
            // Resting on the brink lowers the capsule, but corner sag cannot exceed the capsule radius. A fall drops 2 m.
            Assert.InRange(body.Position.Y, 2.75f - Tuning.CapsuleRadius, 2.76f);
        }
    }

    [Fact]
    public void OpenGroundReachesRangeWithoutAPlanner()
    {
        var driver = new DirectMoveToRange(Options);
        ReachTarget target = ReachTarget.Box(new(6f, 0.75f, 0f), new(0.5f, 0.75f, 0.5f));
        MoveState body = MoveToRangeTests.Body();
        bool arrived = false;
        for (int tick = 0; tick < 300; tick++)
        {
            RangeSteering steering = Tick(driver, body, target, range: 1.5f);
            if (steering.Status == RangeMoveStatus.InRange)
            {
                arrived = true;
                break;
            }
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            body = NpcGroundMovement.Step(body, steering, false, Dt, Tuning, Flat);
        }
        Assert.True(arrived);
        MovementBody shape = MoveToRangeTests.Shape(body, Tuning);
        Assert.True(ReachGeometry.Within(shape, target, 1.5f));
        Assert.InRange(ReachGeometry.Distance(shape, target), 1.5f - Tuning.WalkSpeed * Dt, 1.5f);
    }

    [Fact]
    public void FinalStepShrinksToTheRing()
    {
        var driver = new DirectMoveToRange(Options);
        ReachTarget target = ReachTarget.Point(new(1f, 0.75f, 0f));
        MoveState body = MoveToRangeTests.Body(0.25f);
        Assert.False(ReachGeometry.Within(MoveToRangeTests.Shape(body, Tuning), target, 0.5f));
        RangeSteering steering = Tick(driver, body, target);
        Assert.Equal(RangeMoveStatus.Following, steering.Status);
        Assert.InRange(steering.WorldDirection.Length(), 0.000001f, 0.999f);
        MoveState moved = Flat.Step(body, steering.WorldDirection, false, Dt, Tuning);
        Assert.True(ReachGeometry.Within(MoveToRangeTests.Shape(moved, Tuning), target, 0.5f));
        Assert.Equal(RangeMoveStatus.InRange, Tick(driver, moved, target).Status);
    }

    [Theory]
    [InlineData(0, 0.1f, 45, 0.1f, "stallWindowTicks")]
    [InlineData(-1, 0.1f, 45, 0.1f, "stallWindowTicks")]
    [InlineData(15, 0f, 45, 0.1f, "stallTravelMetres")]
    [InlineData(15, -0.1f, 45, 0.1f, "stallTravelMetres")]
    [InlineData(15, float.NaN, 45, 0.1f, "stallTravelMetres")]
    [InlineData(15, float.PositiveInfinity, 45, 0.1f, "stallTravelMetres")]
    [InlineData(15, 0.1f, 0, 0.1f, "approachWindowTicks")]
    [InlineData(15, 0.1f, -45, 0.1f, "approachWindowTicks")]
    [InlineData(15, 0.1f, 45, 0f, "approachGainMetres")]
    [InlineData(15, 0.1f, 45, -0.1f, "approachGainMetres")]
    [InlineData(15, 0.1f, 45, float.NaN, "approachGainMetres")]
    [InlineData(15, 0.1f, 45, float.NegativeInfinity, "approachGainMetres")]
    [InlineData(65536, 0.1f, 45, 0.1f, "stallWindowTicks")]
    [InlineData(15, 0.1f, 65536, 0.1f, "approachWindowTicks")]
    [InlineData(int.MaxValue, 0.1f, int.MaxValue, 0.1f, "stallWindowTicks")]
    public void OptionsRequirePositiveFiniteThresholds(int stallTicks, float travel, int approachTicks, float gain,
        string parameter)
    {
        var refused = Assert.Throws<ArgumentOutOfRangeException>(
            () => new DirectApproachOptions(stallTicks, travel, approachTicks, gain));
        Assert.Equal(parameter, refused.ParamName);
    }

    [Fact]
    public void WindowsUpToTheBoundBuildADriver()
    {
        var options = new DirectApproachOptions(65535, 0.1f, 65535, 0.1f);
        Assert.Equal(65535, options.StallWindowTicks);
        Assert.Equal(65535, options.ApproachWindowTicks);
        var driver = new DirectMoveToRange(options);
        Assert.Equal(RangeMoveStatus.Following, Tick(driver, MoveToRangeTests.Body(), Far).Status);
    }

    [Fact]
    public void WarmedSteadyTicksAllocateNothing()
    {
        ReachTarget centre = ReachTarget.Point(new(0f, 0.75f, 0f));
        ReachTarget ring = ReachTarget.Point(new(1f, 0.75f, 0f));
        MoveState near = MoveToRangeTests.Body(0.25f);
        var orbit = new DirectMoveToRange(Options);
        var final = new DirectMoveToRange(Options);
        int tick = 0, following = 0;
        void Steady(int count)
        {
            for (int i = 0; i < count; i++)
            {
                // Recorded Following ticks on an orbit, plus a stop ring bisection and Reset.
                if (Tick(orbit, Orbit(++tick), centre, targetMoves: true).Status == RangeMoveStatus.Following) following++;
                RangeSteering shrunk = Tick(final, near, ring);
                if (shrunk.Status == RangeMoveStatus.Following && shrunk.WorldDirection.Length() < 1f) following++;
                final.Reset();
            }
        }
        Steady(60);

        AllocAssert.NoPerCallAllocation("steady DirectMoveToRange.Tick", () => Steady(60));
        Assert.Equal(2 * tick, following);
    }

    [Fact]
    public void BlockedRequestsIdleFromBothAdapters()
    {
        var blocked = new RangeSteering(new Vector2(0.6f, -0.8f), RangeMoveStatus.Blocked);
        MoveCommand command = PlayerPathMovement.Command(blocked, true, 0.7f);
        Assert.Equal(Vector2.Zero, command.Move);
        Assert.True(command.ScaleSpeedByAxis);

        MoveState body = MoveToRangeTests.Body(1f, -1f);
        body.FacingYaw = 1.25f;
        MoveState held = NpcGroundMovement.Hold(body, Dt, Tuning, Flat);
        Assert.Equal(held, NpcGroundMovement.Step(body, blocked, false, Dt, Tuning, Flat));
    }

    private static RangeSteering Tick(DirectMoveToRange driver, in MoveState body, in ReachTarget target,
        float range = 0.5f, bool targetMoves = false)
        => driver.Tick(body, Tuning, target, range, false, targetMoves, Dt, Flat);

    // A 0.05 m step on a 3 m circle: 15 intervals span about 0.75 m and the reach distance never changes.
    private static MoveState Orbit(int tick)
    {
        float angle = tick * (0.05f / 3f);
        return MoveToRangeTests.Body(3f * MathF.Cos(angle), 3f * MathF.Sin(angle));
    }

    private static MoveState Suspended(float x, bool committed)
    {
        MoveState body = MoveToRangeTests.Body(x);
        if (committed) body.Commitment = new MovementCommitment { Phase = MovementCommitmentPhase.Recovering };
        else body.Grounded = false;
        return body;
    }

    private static ReachTarget Translated(in ReachTarget target, Vector3 offset) => target.Kind switch
    {
        ReachTargetKind.Capsule => ReachTarget.Capsule(new MovementBody(target.Centre + offset,
            target.Body.Radius, target.Body.HalfHeight)),
        ReachTargetKind.Box => ReachTarget.Box(target.Centre + offset, target.HalfExtents, target.YawRadians),
        _ => ReachTarget.Point(target.Centre + offset),
    };

    private static void AssertStallLatchesAfter(DirectMoveToRange driver, MoveState body, ReachTarget target,
        float range, MoveTuning tuning, int ticks)
    {
        for (int tick = 1; tick <= ticks; tick++)
        {
            RangeSteering steering = driver.Tick(body, tuning, target, range, false, false, Dt, Flat);
            Assert.Equal(tick < ticks ? RangeMoveStatus.Following : RangeMoveStatus.Blocked, steering.Status);
        }
    }
}
