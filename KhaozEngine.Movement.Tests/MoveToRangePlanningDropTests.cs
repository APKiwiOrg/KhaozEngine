using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.SegmentAdmissionFixture;

namespace KhaozEngine.Tests.Movement;

public sealed partial class MoveToRangePlanningTests
{
    [Fact]
    public void PendingRefreshOfSupportedDropHoldsAndRetainsTheStallWindow()
    {
        using var fixture = new ContactDropFixture();
        MoveTuning tuning = ContactDropFixture.Tuning;
        var navigation = fixture.Capture.BuildProfile(tuning, default, new GroundProfileOptions { MaxDropMetres = 6 });
        using var pool = navigation.CreatePlanningWorkspaces(1);
        var mover = new MoveToRange(navigation, new PathFollowConfig
        {
            Planning = new(new(int.MaxValue), pool),
            ReplanCooldownSeconds = 30f,
            GoalRetargetTolerance = 0.1f,
        }, Stall);
        var body = new MoveState { Position = new(-0.25f, 4.875f, 0), Grounded = true, SpeedScale = 1f };
        var below = ReachTarget.Point(new(1.25f, 0.75f, 0));
        var behind = ReachTarget.Point(new(-1.25f, 4.75f, 0));
        NavPath? initial = null;
        for (int call = 1; call <= 10; call++)
        {
            Assert.Equal(RangeMoveStatus.Following, TickDrop(below).Status);
            initial ??= mover.ActivePathForTests;
            Assert.Same(initial, mover.ActivePathForTests);
            Assert.Equal(NavWaypointKind.Drop, mover.ActivePathForTests!.Waypoints[mover.ActiveWaypointIndexForTests].Kind);
        }

        // Protect the workspace across the retarget. The following call starts a queued request and
        // must drop both the route and its supported preparation without dropping the stall samples.
        var planner = (IIncrementalRegionPathPlanner)navigation.Planner;
        Vector3 feet = new(-0.25f, 4f, 0f);
        using var other = planner.BeginPath(feet, Goal(feet), tuning.CapsuleRadius, PathQueryBudget.Default, pool);
        ExpectPending();
        Assert.Equal(1, pool.Waits);
        other.Cancel();
        ExpectPending();
        for (int call = 1; call <= 6; call++)
        {
            RangeSteering steer = TickDrop(behind);
            Assert.Equal(call == 6 ? RangeMoveStatus.Blocked : RangeMoveStatus.Following, steer.Status);
            Assert.False(mover.PlanningPending);
            Assert.DoesNotContain(mover.ActivePathForTests!.Waypoints, waypoint => waypoint.Kind == NavWaypointKind.Drop);
            if (call < 6) Assert.True(steer.WorldDirection.X < 0f);
        }

        RangeSteering TickDrop(ReachTarget target)
            => mover.Tick(body, tuning, target, 0.01f, false, 1f / 30, fixture.Context);

        void ExpectPending()
        {
            AssertSteering(new(Vector2.Zero, RangeMoveStatus.WaitingForPath), TickDrop(behind));
            Assert.True(mover.PlanningPending);
            Assert.Null(mover.ActivePathForTests);
        }
    }
}
