using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.MoveToRangeCarryTests;
using static KhaozEngine.Tests.Movement.MoveToRangeTests;

namespace KhaozEngine.Tests.Movement;

[Collection("AllocSensitive")]
public class MoveToRangeStallTests
{
    const float Dt = 1f / 30f;
    const float Range = 0.5f;
    // A context guards against overlapping steps and test classes run in parallel, so this class keeps its own.
    static readonly GroundMoveContext Flat = new((_, _) => 0f);
    static readonly Func<Vector3, Vector3, bool> Open = (_, _) => true;
    static readonly RouteApproachOptions Stall15 = new() { Stall = new(15, 0.1f) };
    static readonly ReachTarget Far = ReachTarget.Point(new(6f, 0.75f, 0f));

    [Fact]
    public void DefaultRouteOptionsHaveNoStall()
    {
        Assert.Null(RouteApproachOptions.Default.Stall);
        Assert.Null(new RouteApproachOptions().Stall);
    }

    [Fact]
    public void DefaultOptionsNeverBlock()
    {
        var legacy = new MoveToRange(ToAnchor(), Space, Open);
        var stated = new MoveToRange(ToAnchor(), Space, Open, null, RouteApproachOptions.Default);
        // The control: the same comparison sees a stalling mover on this fixture, so the equality can fail.
        MoveToRange control = Mover(Stall15);
        bool diverged = false;
        for (int tick = 0; tick < 40; tick++)
        {
            RangeSteering old = Tick(legacy, Body(), Far);
            RangeSteering now = Tick(stated, Body(), Far);
            Assert.Equal(old.Status, now.Status);
            Assert.True(SameBits(old.WorldDirection, now.WorldDirection), $"tick {tick} steered differently");
            Assert.NotEqual(RangeMoveStatus.Blocked, old.Status);
            diverged |= Tick(control, Body(), Far).Status != old.Status;
        }
        Assert.True(diverged);
    }

    [Fact]
    public void StallLatchesOnTheSixteenthCountedTick()
    {
        MoveToRange mover = Mover(Stall15);
        for (int tick = 1; tick <= 30; tick++)
        {
            RangeSteering steering = Tick(mover, Body(0.005f * tick), Far);
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
        mover.Reset();
        Assert.Equal(RangeMoveStatus.Following, Tick(mover, Body(0.15f), Far).Status);
    }

    [Fact]
    public void WaitingForPathTicksCount()
    {
        var mover = new MoveToRange(new ScriptPlanner((_, _) => Route(NavPathStatus.Partial, Vector2.Zero)), Space,
            Open, new PathFollowConfig { ReplanCooldownSeconds = 10f }, Stall15);
        for (int counted = 1; counted <= 20; counted++)
        {
            RangeSteering steering = Tick(mover, Body(), Far);
            Assert.Equal(counted < 16 ? RangeMoveStatus.WaitingForPath : RangeMoveStatus.Blocked, steering.Status);
            Assert.Equal(Vector2.Zero, steering.WorldDirection);
        }
    }

    [Fact]
    public void ZeroTravelBoundAndSuspendedTicksCountTowardNothing()
    {
        MoveToRange mover = Mover(Stall15);
        for (int tick = 0; tick < 10; tick++)
            Assert.Equal(RangeMoveStatus.Following, Tick(mover, Body(), Far).Status);
        // Counted rooted ticks at the stalled spot would latch on the sixth of them.
        MoveState rooted = Body();
        rooted.SpeedScale = 0f;
        for (int tick = 0; tick < 20; tick++)
        {
            RangeSteering held = Tick(mover, rooted, Far);
            Assert.Equal(RangeMoveStatus.Following, held.Status);
            Assert.Equal(Vector2.Zero, held.WorldDirection);
        }
        MoveState airborne = Body();
        airborne.Grounded = false;
        for (int tick = 0; tick < 20; tick++)
            Assert.Equal(RangeMoveStatus.Suspended, Tick(mover, airborne, Far).Status);
        for (int held = 1; held <= 6; held++)
            Assert.Equal(held < 6 ? RangeMoveStatus.Following : RangeMoveStatus.Blocked, Tick(mover, Body(), Far).Status);
    }

    [Fact]
    public void InRangeClearsTheWindow()
    {
        MoveToRange mover = Mover(Stall15);
        for (int tick = 0; tick < 10; tick++)
            Assert.Equal(RangeMoveStatus.Following, Tick(mover, Body(), Far).Status);
        Assert.Equal(RangeMoveStatus.InRange, Tick(mover, Body(5.8f), Far).Status);
        AssertLatchesOnTheSixteenth(mover, Far);
    }

    [Fact]
    public void ShapeChangeUnlatchesAndTranslationDoesNot()
    {
        // Review Focus 5.
        MoveToRange mover = Mover(Stall15);
        var half = new Vector3(0.25f, 0.5f, 0.25f);
        AssertLatchesOnTheSixteenth(mover, ReachTarget.Box(new(6f, 0.75f, 0f), half));

        ReachTarget moved = ReachTarget.Box(new(6f, 0.75f, 1f), half);
        RangeSteering translated = Tick(mover, Body(), moved);
        Assert.Equal(RangeMoveStatus.Blocked, translated.Status);
        Assert.Equal(Vector2.Zero, translated.WorldDirection);

        AssertLatchesOnTheSixteenth(mover, moved, 0.6f);
        ReachTarget point = ReachTarget.Point(new(6f, 0.75f, 1f));
        AssertLatchesOnTheSixteenth(mover, point, 0.6f);
        AssertLatchesOnTheSixteenth(mover, point, 0.6f, Tuning with { CapsuleRadius = Tuning.CapsuleRadius + 0.05f });
    }

    [Fact]
    public void ARoutedDetourIsNotBlocked()
    {
        NavSpace space = Surfaces(-4f, -4f, (x, z) => !(x is 8 or 9 && z is >= 0 and <= 14));
        Vector2 start = Centre(4, 8), end = Centre(14, 8);
        var mover = new MoveToRange(new GridPathPlanner(space), space, Open, null, Stall15);
        ReachTarget target = ReachTarget.Point(new(end.X, 0.75f, end.Y));
        MoveState body = Body(start.X, start.Y);
        var distances = new List<float>();
        bool arrived = false;
        for (int tick = 0; tick < 300 && !arrived; tick++)
        {
            distances.Add(ReachGeometry.Distance(Shape(body, Tuning), target));
            RangeSteering steering = mover.Tick(body, Tuning, target, 0f, false, Dt, Flat);
            arrived = steering.Status == RangeMoveStatus.InRange;
            if (arrived) break;
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            body = NpcGroundMovement.Step(body, steering, false, Dt, Tuning, Flat);
        }

        Assert.True(arrived);
        // The detour leads away from the target, so a reach window would fault it where the stall window does not.
        Assert.Contains(Enumerable.Range(1, 29), tick => distances[tick] > distances[tick - 1]);
    }

    [Fact]
    public void TheStraighteningFallbackAloneNeverLatches()
    {
        MoveToRangeStraightenTests.Driver drive = MoveToRangeStraightenTests.FallBackDrive(
            out MoveToRangeStraightenTests.SeenCells seen, new RouteApproachOptions { StraightenRoutes = true, Stall = Stall15.Stall },
            Flat);

        Assert.True(seen.Refusals > 0);
        Assert.Equal(RangeMoveStatus.InRange, drive.Steering[^1].Status);
        Assert.DoesNotContain(drive.Steering, steering => steering.Status == RangeMoveStatus.Blocked);
        // The refused straight step is the one counted zero travel tick, and the raw replan moves.
        Assert.Equal(1, drive.Steering.Count(steering =>
            steering.Status == RangeMoveStatus.Following && steering.WorldDirection == Vector2.Zero));
    }

    [Fact]
    public void ARefusedRawLegLatchesBlocked()
    {
        // The #1278 hold on RefusedRawStepKeepsTheRoute's fixture. The unarmed first tick is counted tick 1, the
        // straightened step the armed guard refuses is counted tick 2, and every raw step after it is refused.
        NavSpace space = Surfaces(146f, 146f);
        bool armed = false;
        var drive = new MoveToRangeStraightenTests.Driver(space, (_, _) => !armed,
            new RouteApproachOptions { StraightenRoutes = true, Stall = Stall15.Stall }, MoveToRangeStraightenTests.StairStart, Flat);
        Vector2 end = MoveToRangeStraightenTests.StairEnd;
        ReachTarget target = ReachTarget.Point(new(end.X, 0.75f, end.Y));
        Assert.Equal(RangeMoveStatus.Following, drive.Mover.Tick(drive.Body, Tuning, target, 0f, false, Dt, Flat).Status);
        armed = true;

        for (int counted = 2; counted <= 30; counted++)
        {
            RangeSteering steering = drive.Tick(end);
            Assert.Equal(counted < 16 ? RangeMoveStatus.Following : RangeMoveStatus.Blocked, steering.Status);
            Assert.Equal(Vector2.Zero, steering.WorldDirection);
        }

        Assert.Equal(2, drive.Recorder.Plans.Count);
        Assert.Equal(MoveToRangeStraightenTests.StairStart, new Vector2(drive.Body.Position.X, drive.Body.Position.Z));
    }

    [Fact]
    public void WarmedStallTicksAllocateNothing()
    {
        // The first mover warms every path the measured one takes: counted ticks, the latch and latched holds.
        (MoveToRange warm, MoveState warmBody) = Walked();
        (MoveToRange mover, MoveState body) = Walked();
        int following = 0, blocked = 0;
        void Held(MoveToRange driver, MoveState at)
        {
            for (int tick = 0; tick < 200; tick++)
            {
                RangeMoveStatus status = Tick(driver, at, Far).Status;
                if (status == RangeMoveStatus.Following) following++;
                else if (status == RangeMoveStatus.Blocked) blocked++;
            }
        }
        Held(warm, warmBody);
        following = blocked = 0;

        AllocAssert.NoPerCallAllocation("warmed MoveToRange.Tick with Stall", () => Held(mover, body));
        Assert.True(following > 0, "no counted tick was measured");
        Assert.True(blocked > 0, "no latched tick was measured");
    }

    static (MoveToRange Mover, MoveState Body) Walked()
    {
        MoveToRange mover = Mover(Stall15);
        MoveState body = Body();
        for (int tick = 0; tick < 20; tick++)
        {
            RangeSteering steering = Tick(mover, body, Far);
            Assert.Equal(RangeMoveStatus.Following, steering.Status);
            body = NpcGroundMovement.Step(body, steering, false, Dt, Tuning, Flat);
        }
        return (mover, body);
    }

    // Counted ticks with the body held at x 0: Following on the first fifteen, Blocked on the sixteenth.
    static void AssertLatchesOnTheSixteenth(MoveToRange mover, ReachTarget target, float range = Range,
        MoveTuning? tuning = null)
    {
        for (int counted = 1; counted <= 16; counted++)
        {
            RangeSteering steering = Tick(mover, Body(), target, range, tuning);
            Assert.Equal(counted < 16 ? RangeMoveStatus.Following : RangeMoveStatus.Blocked, steering.Status);
            if (counted == 16) Assert.Equal(Vector2.Zero, steering.WorldDirection);
        }
    }

    static MoveToRange Mover(RouteApproachOptions options) => new(ToAnchor(), Space, Open, null, options);

    // The open Space has no surface heights to plan a region on, so the route runs straight to the goal anchor.
    static ScriptPlanner ToAnchor()
        => new((_, goal) => Route(NavPathStatus.Complete, new Vector2(goal.Anchor.X, goal.Anchor.Z)));

    static RangeSteering Tick(MoveToRange mover, MoveState body, ReachTarget target, float range = Range,
        MoveTuning? tuning = null)
        => mover.Tick(body, tuning ?? Tuning, target, range, false, Dt, Flat);

    static Vector2 Centre(int x, int z) => new(-4f + (x + 0.5f) * 0.25f, -4f + (z + 0.5f) * 0.25f);

    static bool SameBits(Vector2 a, Vector2 b)
        => BitConverter.SingleToInt32Bits(a.X) == BitConverter.SingleToInt32Bits(b.X)
            && BitConverter.SingleToInt32Bits(a.Y) == BitConverter.SingleToInt32Bits(b.Y);
}
