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
        NavGoalRegion taken = Assert.Single(regions);

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
    [InlineData("capsule")]
    [InlineData("point")]
    [InlineData("yawed box")]
    public void SnapshotMatchesLiveReach(string kind)
    {
        ReachTarget target = kind switch
        {
            "capsule" => ReachTarget.Capsule(new MovementBody(new(2f, 1.2f, 0.3f), 0.35f, 0.9f)),
            "point" => ReachTarget.Point(new(2f, 0.75f, 0.3f)),
            _ => ReachTarget.Box(new(2f, 0.75f, 0.3f), new(0.6f, 0.5f, 0.25f), 0.7f),
        };
        var snapshot = new ReachGoalSnapshot(target, Range, Tuning.CapsuleRadius, Tuning.CapsuleHalfHeight);
        int inside = 0, outside = 0;
        foreach (float y in new[] { -1.5f, 0f, 0.9f })
            for (int x = 0; x <= 40; x++)
                for (int z = 0; z <= 20; z++)
                {
                    Vector3 feet = new(0.5f + x * 0.075f, y, -0.9f + z * 0.12f);
                    bool live = Reach(feet, Tuning, target, Range);
                    Assert.Equal(live, snapshot.Contains(feet));
                    if (live) inside++;
                    else outside++;
                }
        Assert.True(inside > 0, $"no {kind} probe was in reach");
        Assert.True(outside > 0, $"every {kind} probe was in reach");
    }

    // A chasing mover makes one goal for each target it is given and nothing while the target holds still.
    [Fact]
    public void ChasingMoverAllocatesOneGoalPerAnchorChange()
    {
        const int Moves = 30;
        NavPath route = Route(NavPathStatus.Complete, new Vector2(6f, 0f));
        MoveToRange Chaser() => new(new ScriptPlanner((_, _) => route), Space, Open);
        MoveState Chase(MoveToRange mover, MoveState body, int ticks, bool moving)
        {
            for (int tick = 0; tick < ticks; tick++)
            {
                // A still target holds where the moving one stopped, so its first tick is no anchor change.
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
        void Make() => made = new NavGoalRegion(Vector3.Zero, 1f,
            new ReachGoalSnapshot(First, Range, Tuning.CapsuleRadius, Tuning.CapsuleHalfHeight).Contains);
        Make();
        long goal = AllocationMeasurement.Measure("one goal region with its reach snapshot", Make);
        Assert.NotNull(made);

        MoveToRange mover = Chaser();
        MoveState body = Body();
        long chased = AllocationMeasurement.Measure("MoveToRange.Tick chasing a moving target",
            () => body = Chase(mover, body, Moves, true));
        Assert.True(chased <= Moves * goal, $"{Moves} anchor changes allocated {chased} bytes, one goal is {goal}");
        AllocAssert.NoPerCallAllocation("MoveToRange.Tick on a still target", () => body = Chase(mover, body, Moves, false));
    }

    static bool Reach(Vector3 feet, in MoveTuning tuning, in ReachTarget target, float range)
        => ReachGeometry.Within(new MovementBody(feet + new Vector3(0f, tuning.CapsuleHalfHeight, 0f),
            tuning.CapsuleRadius, tuning.CapsuleHalfHeight), target, range);
}
