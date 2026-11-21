using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.MoveToRangeTests;

namespace KhaozEngine.Tests.Movement;

/// <summary>A goal region owns the reach it was made for, so a query or request that outlives the tick that
/// began it keeps its members after the mover's target, range or tuning changes.</summary>
[Collection("AllocSensitive")]
public class MoveToRangeGoalSnapshotTests
{
    const float Dt = 1f / 30f;
    const float Range = 0.5f;
    // A context guards against overlapping steps and test classes run in parallel, so this class keeps its own.
    static readonly GroundMoveContext Flat = new((_, _) => 0f);
    static readonly Func<Vector3, Vector3, bool> Open = (_, _) => true;
    static readonly ReachTarget First = ReachTarget.Point(new(2f, 0.75f, 0f));

    [Theory]
    [InlineData("translation")]
    [InlineData("range")]
    [InlineData("tuning")]
    public void GoalPredicateIgnoresLaterRetarget(string change)
    {
        var regions = new List<NavGoalRegion>();
        NavPath route = Route(NavPathStatus.Complete, new Vector2(2f, 0f));
        var mover = new MoveToRange(new ScriptPlanner((_, goal) =>
        {
            regions.Add(goal);
            return route;
        }), Space, Open);
        Assert.Equal(RangeMoveStatus.Following, mover.Tick(Body(), Tuning, First, Range, false, Dt, Flat).Status);
        NavGoalRegion taken = regions[0];

        (ReachTarget next, float range, MoveTuning tuning) = change switch
        {
            "translation" => (ReachTarget.Point(new(2.6f, 0.75f, 0f)), Range, Tuning),
            "range" => (First, 0.1f, Tuning),
            _ => (First, Range, Tuning with { CapsuleRadius = 0.05f }),
        };
        mover.Tick(Body(), tuning, next, range, false, Dt, Flat);

        int changed = 0;
        for (int step = 0; step <= 28; step++)
        {
            Vector3 feet = new(0.8f + step * 0.1f, 0f, 0f);
            bool before = Reach(feet, Tuning, First, Range);
            Assert.Equal(before, taken.Contains(feet));
            if (before != Reach(feet, tuning, next, range)) changed++;
        }
        // The control: the retarget moves the reach boundary across some probe, so a live predicate would fail here.
        Assert.True(changed > 0, $"the {change} retarget changed no probe's reach");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnchorHoldingReachChangeMakesANewRegion(bool flipAnchorZero)
    {
        const float halfHeight = 0.875f;
        float centreY = MathF.BitDecrement(0.125f);
        float nextCentreY = MathF.BitIncrement(centreY);
        Assert.Equal(BitConverter.SingleToInt32Bits(centreY - halfHeight),
            BitConverter.SingleToInt32Bits(nextCentreY - halfHeight));

        ReachTarget first = ReachTarget.Capsule(new MovementBody(new(2f, centreY, 0f), 0.25f, halfHeight));
        float nextZ = flipAnchorZero ? BitConverter.Int32BitsToSingle(int.MinValue) : 0f;
        ReachTarget next = ReachTarget.Capsule(new MovementBody(new(2f, nextCentreY, nextZ), 0.25f, halfHeight));
        Assert.Equal(flipAnchorZero ? int.MinValue : 0,
            BitConverter.SingleToInt32Bits(ReachGoalSnapshot.AnchorFor(next).Z));
        NavGoalRegion? queried = null;
        var mover = new MoveToRange(new ScriptPlanner((_, goal) =>
        {
            queried = goal;
            return NavPath.Unreachable;
        }), Space, Open, new PathFollowConfig { ReplanCooldownSeconds = 0f });

        // An unreachable route is eligible for another query when the cooldown has expired.
        mover.Tick(Body(), Tuning, first, Range, false, Dt, Flat);
        NavGoalRegion previous = Assert.IsType<NavGoalRegion>(queried);
        queried = null;
        mover.Tick(Body(), Tuning, next, Range, false, Dt, Flat);
        NavGoalRegion current = Assert.IsType<NavGoalRegion>(queried);

        static (int X, int Y, int Z) AnchorBits(Vector3 anchor) => (BitConverter.SingleToInt32Bits(anchor.X),
            BitConverter.SingleToInt32Bits(anchor.Y), BitConverter.SingleToInt32Bits(anchor.Z));
        Assert.NotSame(previous, current);
        Assert.Equal(AnchorBits(previous.Anchor), AnchorBits(current.Anchor));

        // At this height the second capsule's top is exactly a range below the probe.
        Vector3 probe = new(2f, 1.5f, 0f);
        Assert.False(previous.Contains(probe));
        Assert.True(current.Contains(probe));
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("centre X")]
    [InlineData("centre Y")]
    [InlineData("centre Z")]
    [InlineData("body centre X")]
    [InlineData("body centre Y")]
    [InlineData("body centre Z")]
    [InlineData("body radius")]
    [InlineData("body half-height")]
    [InlineData("half-extents X")]
    [InlineData("half-extents Y")]
    [InlineData("half-extents Z")]
    [InlineData("yaw")]
    [InlineData("range")]
    [InlineData("capsule radius")]
    [InlineData("capsule half-height")]
    [InlineData("signed zero")]
    public void SnapshotMatchesOnlyIdenticalBits(string field)
    {
        // Populate every field independently. The public capsule factory copies Body.Centre into Centre.
        ReachTargetKind kind = ReachTargetKind.Box;
        Vector3 centre = new(2f, 0.75f, 0f);
        Vector3 bodyCentre = new(3f, 1.25f, 0.5f);
        Vector3 extents = new(0.6f, 0.5f, 0.25f);
        float bodyRadius = 0.35f, bodyHalfHeight = 0.9f, yaw = 0.7f, range = Range;
        float radius = Tuning.CapsuleRadius, halfHeight = Tuning.CapsuleHalfHeight;
        ReachTarget Target() => new(kind, new MovementBody(bodyCentre, bodyRadius, bodyHalfHeight), centre, extents, yaw);

        var snapshot = new ReachGoalSnapshot(Target(), range, radius, halfHeight);
        Assert.True(snapshot.Matches(Target(), range, radius, halfHeight));

        switch (field)
        {
            case "kind": kind = ReachTargetKind.Point; break;
            case "centre X": centre.X = MathF.BitIncrement(centre.X); break;
            case "centre Y": centre.Y = MathF.BitIncrement(centre.Y); break;
            case "centre Z": centre.Z = MathF.BitIncrement(centre.Z); break;
            case "body centre X": bodyCentre.X = MathF.BitIncrement(bodyCentre.X); break;
            case "body centre Y": bodyCentre.Y = MathF.BitIncrement(bodyCentre.Y); break;
            case "body centre Z": bodyCentre.Z = MathF.BitIncrement(bodyCentre.Z); break;
            case "body radius": bodyRadius = MathF.BitIncrement(bodyRadius); break;
            case "body half-height": bodyHalfHeight = MathF.BitIncrement(bodyHalfHeight); break;
            case "half-extents X": extents.X = MathF.BitIncrement(extents.X); break;
            case "half-extents Y": extents.Y = MathF.BitIncrement(extents.Y); break;
            case "half-extents Z": extents.Z = MathF.BitIncrement(extents.Z); break;
            case "yaw": yaw = MathF.BitIncrement(yaw); break;
            case "range": range = MathF.BitIncrement(range); break;
            case "capsule radius": radius = MathF.BitIncrement(radius); break;
            case "capsule half-height": halfHeight = MathF.BitIncrement(halfHeight); break;
            case "signed zero": centre.Z = -0f; break;
            default: throw new ArgumentOutOfRangeException(nameof(field));
        }

        Assert.False(snapshot.Matches(Target(), range, radius, halfHeight));
    }

    [Theory]
    [InlineData("capsule")]
    [InlineData("point")]
    [InlineData("yawed box")]
    public void SnapshotMatchesReachAtTheSeatedCentre(string kind)
    {
        ReachTarget target = kind switch
        {
            "capsule" => ReachTarget.Capsule(new MovementBody(new(2f, 1.2f, 0.3f), 0.35f, 0.9f)),
            "point" => ReachTarget.Point(new(2f, 0.75f, 0.3f)),
            _ => ReachTarget.Box(new(2f, 0.75f, 0.3f), new(0.6f, 0.5f, 0.25f), 0.7f),
        };
        var snapshot = new ReachGoalSnapshot(target, Range, Tuning.CapsuleRadius, Tuning.CapsuleHalfHeight);
        int inside = 0, outside = 0;
        // These centres are the float sums of the probe heights and the test capsule's 0.75 m half-height.
        foreach ((float feetY, float centreY) in new[] { (-1.5f, -0.75f), (0f, 0.75f), (0.9f, 1.65f) })
            for (int x = 0; x <= 40; x++)
                for (int z = 0; z <= 20; z++)
                {
                    Vector3 feet = new(0.5f + x * 0.075f, feetY, -0.9f + z * 0.12f);
                    var body = new MovementBody(new(feet.X, centreY, feet.Z), Tuning.CapsuleRadius,
                        Tuning.CapsuleHalfHeight);
                    bool live = ReachGeometry.Within(body, target, Range);
                    Assert.Equal(live, snapshot.Contains(feet));
                    if (live) inside++;
                    else outside++;
                }
        Assert.True(inside > 0, $"no {kind} probe was in reach");
        Assert.True(outside > 0, $"every {kind} probe was in reach");
    }

    // One fresh region and snapshot per change to the reach bits. A held reach needs neither.
    [Fact]
    public void ChasingMoverAllocatesAtMostOneGoalPerReachChange()
    {
        const int Moves = 30;
        NavPath route = Route(NavPathStatus.Complete, new Vector2(6f, 0f));
        MoveToRange Chaser() => new(new ScriptPlanner((_, _) => route), Space, Open);
        MoveState Chase(MoveToRange mover, MoveState body, int ticks, bool moving)
        {
            for (int tick = 0; tick < ticks; tick++)
            {
                // A still target holds where the moving one stopped, so its first tick keeps the same reach.
                float x = 3f + (moving ? tick : Moves - 1) * 0.05f;
                RangeSteering steering = mover.Tick(body, Tuning, ReachTarget.Point(new(x, 0.75f, 0f)), Range,
                    false, Dt, Flat);
                body = NpcGroundMovement.Step(body, steering, false, Dt, Tuning, Flat);
            }
            return body;
        }
        MoveToRange warm = Chaser();
        Chase(warm, Chase(warm, Body(), Moves, true), Moves, false);
        NavGoalRegion? made = null;
        ReachGoalSnapshot? snapshot = null;
        void Make()
        {
            snapshot = new ReachGoalSnapshot(First, Range, Tuning.CapsuleRadius, Tuning.CapsuleHalfHeight);
            made = new NavGoalRegion(Vector3.Zero, 1f, static _ => true);
        }
        Make();
        long goal = AllocationMeasurement.Measure("one goal region with its reach snapshot", Make);
        Assert.NotNull(made);
        Assert.NotNull(snapshot);

        MoveToRange mover = Chaser();
        MoveState body = Body();
        long chased = AllocationMeasurement.Measure("MoveToRange.Tick chasing a moving target",
            () => body = Chase(mover, body, Moves, true));
        Assert.True(chased <= Moves * goal, $"{Moves} reach changes allocated {chased} bytes, one goal is {goal}");
        AllocAssert.NoPerCallAllocation("MoveToRange.Tick on a still target", () => body = Chase(mover, body, Moves, false));
    }

    static bool Reach(Vector3 feet, in MoveTuning tuning, in ReachTarget target, float range)
        => ReachGeometry.Within(new MovementBody(feet + new Vector3(0f, tuning.CapsuleHalfHeight, 0f),
            tuning.CapsuleRadius, tuning.CapsuleHalfHeight), target, range);
}
