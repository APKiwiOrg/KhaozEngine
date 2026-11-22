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
    public void PendingTicksNeitherCountNorClear()
    {
        var navigation = Flat(34, 3, Tuning.CapsuleRadius);
        using var context = new GroundMoveContext((_, _) => 0f);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        var mover = new MoveToRange(navigation, new PathFollowConfig
        {
            Planning = new(new(128), pool),
            ReplanCooldownSeconds = 0f,
        }, Stall);
        MoveState body = At(0.5f);
        for (int call = 1; call <= 10; call++)
            AssertSteering(new(Vector2.UnitX, RangeMoveStatus.Following), Tick(mover, body, Target(1.5f), context));

        Vector3 feet = new(0.5f, 0f, 1.5f);
        var planner = (IIncrementalRegionPathPlanner)navigation.Planner;
        using var other = planner.BeginPath(feet, Goal(feet), Tuning.CapsuleRadius, PathQueryBudget.Default, pool);
        for (int call = 0; call < 30; call++)
            HoldPending(mover, body, Target(31.5f), context);
        other.Cancel();

        for (int call = 0; call < 100 && mover.PlanningPending; call++)
            Assert.NotEqual(RangeMoveStatus.Blocked, Tick(mover, body, Target(31.5f), context).Status);
        AssertRoute(mover, NavPathStatus.Complete,
        [
            1.5f, 2.5f, 3.5f, 4.5f, 5.5f, 6.5f, 7.5f, 8.5f, 9.5f, 10.5f,
            11.5f, 12.5f, 13.5f, 14.5f, 15.5f, 16.5f, 17.5f, 18.5f, 19.5f, 20.5f,
            21.5f, 22.5f, 23.5f, 24.5f, 25.5f, 26.5f, 27.5f, 28.5f, 29.5f, 30.5f, 31.5f,
        ]);
        // Adoption counts once. The earlier ten samples and this one leave exactly five before the latch.
        for (int call = 1; call <= 5; call++)
            Assert.Equal(call == 5 ? RangeMoveStatus.Blocked : RangeMoveStatus.Following,
                Tick(mover, body, Target(31.5f), context).Status);
    }

    [Fact]
    public void FirstCallAdoptionKeepsSynchronousCooldownAndStall()
    {
        var navigation = Flat(6, 3, Tuning.CapsuleRadius);
        using var context = new GroundMoveContext((_, _) => 0f);
        using var pool = navigation.CreatePlanningWorkspaces(1);
        var synchronous = new MoveToRange(navigation, null, Stall);
        var sliced = new MoveToRange(navigation, Follow(pool, int.MaxValue), Stall);
        MoveState body = At(0.5f);

        for (int call = 1; call <= 16; call++)
        {
            ReachTarget target = call == 1 ? Target(2.75f) : Far;
            RangeSteering expected = call == 16
                ? new(Vector2.Zero, RangeMoveStatus.Blocked)
                : new(Vector2.UnitX, RangeMoveStatus.Following);
            AssertSteering(expected, Tick(synchronous, body, target, context));
            AssertSteering(expected, Tick(sliced, body, target, context));
            float[] route = call < 3 ? [1.5f, 2.5f] : [1.5f, 2.5f, 3.5f, 4.5f];
            AssertRoute(synchronous, NavPathStatus.Complete, route);
            AssertRoute(sliced, NavPathStatus.Complete, route);
            Assert.False(sliced.PlanningPending);
        }
    }
}
