using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;

namespace KhaozEngine.Tests.Movement;

public sealed partial class MoveToRangePlanningTests
{
    [Fact]
    public void RefusedStraightenedStepReplansRawOnceThroughThePool()
    {
        var navigation = Flat(6, 3, Tuning.CapsuleRadius);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        var planner = (IIncrementalRegionPathPlanner)navigation.Planner;
        var mover = new MoveToRange(navigation, Follow(pool, int.MaxValue), Straight);
        var held = At(0.5f);
        bool refuse = false;
        using var context = new GroundMoveContext((_, _) => 0f,
            clampXz: (x, z) => new(x, refuse ? -1f : z));
        AssertSteering(new(Vector2.UnitX, RangeMoveStatus.Following), Tick(mover, held, Far, context));
        AssertRoute(mover, NavPathStatus.Complete, [4.5f]);

        refuse = true;
        AssertSteering(new(Vector2.Zero, RangeMoveStatus.Following), Tick(mover, held, Far, context));
        Assert.Null(mover.ActivePathForTests);
        Assert.False(mover.PlanningPending);
        refuse = false;

        var feet = new Vector3(0.5f, 0f, 1.5f);
        using var other = planner.BeginPath(feet, Goal(feet), Tuning.CapsuleRadius, PathQueryBudget.Default, pool);
        HoldPending(mover, held, Far, context);
        Assert.Equal(1, pool.Waits);
        other.Cancel();
        HoldPending(mover, held, Far, context);
        AssertSteering(new(Vector2.UnitX, RangeMoveStatus.Following), Tick(mover, held, Far, context));
        AssertRoute(mover, NavPathStatus.Complete, [1.5f, 2.5f, 3.5f, 4.5f]);
        Assert.Null(mover.Straightener!.LastStraightened);

        mover.Reset();
        AssertSteering(new(Vector2.UnitX, RangeMoveStatus.Following), Tick(mover, held, Far, context));
        AssertRoute(mover, NavPathStatus.Complete, [4.5f]);
    }
}
