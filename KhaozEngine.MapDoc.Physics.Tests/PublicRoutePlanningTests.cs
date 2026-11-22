// This file lives here so it compiles without friend access to Navigation or Movement.
using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.MapDoc.Physics;

public sealed class PublicRoutePlanningTests
{
    static readonly MoveTuning Tuning = MoveTuning.Default;
    static readonly Vector3 Start = new(1.5f, 0f, 1.5f);
    const float Dt = 1f / 30f;

    [Fact]
    public void FollowerAndStraightenedMoverShareAProfilePool()
    {
        using var world = FlatFloor();
        using var context = new GroundMoveContext((_, _) => 0f, physics: world);
        GroundNavigation profile = BuildProfile(context);
        Assert.True(profile.SupportsPlanningRequests);

        var destination = new Vector3(7.5f, 0f, 1.5f);
        var goal = new NavGoalRegion(destination, 0f, feet => feet.X == destination.X && feet.Z == destination.Z);
        NavPath expectedRoute = profile.Planner.FindPath(Start, goal, Tuning.CapsuleRadius, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, expectedRoute.Status);

        var options = new RouteApproachOptions { StraightenRoutes = true };
        var body = new MoveState { Position = Start + Vector3.UnitY * Tuning.CapsuleHalfHeight, Grounded = true, SpeedScale = 1f };
        var target = ReachTarget.Point(new Vector3(6.5f, Tuning.CapsuleHalfHeight, 3.5f));
        var synchronousMover = new MoveToRange(profile, null, options);
        var rawMover = new MoveToRange(profile);
        RangeSteering expectedSteering = Steer(synchronousMover);
        Assert.Equal(RangeMoveStatus.Following, expectedSteering.Status);
        Assert.NotEqual(Steer(rawMover).WorldDirection, expectedSteering.WorldDirection);

        using PathPlanWorkspaces pool = profile.CreatePlanningWorkspaces(2);
        Assert.Equal(2, pool.Count);
        var config = new PathFollowConfig
        {
            AcceptRadius = 0f,
            Planning = new PathPlanningConfig(new PathWorkBudget(PathPlanningConfig.MinimumQuotaUnits), pool),
        };
        var follower = new PathFollower(profile.Planner, config);
        var mover = new MoveToRange(profile, config, options);
        try
        {
            PathFollowOutput first = follower.Tick(Start, goal, Tuning.CapsuleRadius, Dt);
            Assert.Equal(PathFollowState.WaitingForPath, first.State);
            Assert.Equal(Vector2.Zero, first.WorldDir);
            Assert.True(follower.PlanningPending);
            Assert.Null(follower.ActivePath);
            Assert.Equal(new RangeSteering(Vector2.Zero, RangeMoveStatus.WaitingForPath), Steer(mover));
            Assert.True(mover.PlanningPending);

            bool arrived = false, adopted = false;
            NavPath? route = null;
            Vector3 feet = Start;
            for (int call = 0; call < 2048 && (!arrived || !adopted); call++)
            {
                if (!arrived)
                {
                    PathFollowOutput step = follower.Tick(feet, goal, Tuning.CapsuleRadius, Dt);
                    route ??= follower.ActivePath;
                    arrived = step.State == PathFollowState.Arrived;
                    feet += new Vector3(step.WorldDir.X, 0f, step.WorldDir.Y) * 0.5f;
                }
                if (!adopted)
                {
                    RangeSteering step = Steer(mover);
                    adopted = !mover.PlanningPending;
                    if (adopted) Assert.Equal(expectedSteering, step);
                    else Assert.Equal(new RangeSteering(Vector2.Zero, RangeMoveStatus.WaitingForPath), step);
                }
            }

            Assert.True(arrived);
            Assert.True(adopted);
            Assert.False(follower.PlanningPending);
            NavPath actual = Assert.IsType<NavPath>(route);
            Assert.Equal(expectedRoute.Status, actual.Status);
            Assert.Equal<NavWaypoint>(expectedRoute.Waypoints, actual.Waypoints);
            Assert.Equal(0, pool.Waits);
        }
        finally
        {
            follower.Reset();
            mover.Reset();
            synchronousMover.Reset();
            rawMover.Reset();
        }

        RangeSteering Steer(MoveToRange owner) => owner.Tick(body, Tuning, target, 0f, false, Dt, context);
    }

    [Fact]
    public void ReportedMinimumQuotaIsAcceptedAndThePreviousUnitIsRefused()
    {
        using var world = FlatFloor();
        using var context = new GroundMoveContext((_, _) => 0f, physics: world);
        GroundNavigation profile = BuildProfile(context);
        using PathPlanWorkspaces pool = profile.CreatePlanningWorkspaces(1, PathQueryBudget.Default);

        int minimum = PathPlanningConfig.MinimumQuotaUnits;
        var config = new PathPlanningConfig(new PathWorkBudget(minimum), pool);
        Assert.Equal(minimum, config.Quota.Units);
        Assert.Same(pool, config.Workspaces);
        Assert.Throws<ArgumentOutOfRangeException>("quota",
            () => new PathPlanningConfig(new PathWorkBudget(minimum - 1), pool));
    }

    [Fact]
    public void AreaExcludingProfileDeclinesPlanningWorkspaces()
    {
        using var world = FlatFloor();
        using var context = new GroundMoveContext((_, _) => 0f, physics: world);
        GroundNavigation view = BuildProfile(context).ExcludingAreas(1u);

        Assert.False(view.SupportsPlanningRequests);
        Assert.Throws<NotSupportedException>(() => view.CreatePlanningWorkspaces(1));
    }

    static BepuPhysicsWorld FlatFloor()
    {
        var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(8f, 0.125f, 8f)), Pose.At(new Vector3(0f, -0.125f, 0f)));
        return world;
    }

    static GroundNavigation BuildProfile(GroundMoveContext context)
    {
        using var bake = PhysicsNavBake.Capture(context, new(-1f, -1f, 8f, 6f, 1f, 3f, 6f,
            Tuning.MaxSlopeRadians, 256, 256), _ => 1u);
        return bake.BuildProfile(Tuning, default);
    }
}
