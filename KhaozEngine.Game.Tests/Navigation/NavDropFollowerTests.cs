using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavDropFollowerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActiveDropCannotBeConsumedOrRetargetedWithoutAcknowledgement(bool region)
    {
        var (space, _) = NavDropPlannerTests.Chain();
        var planner = new FixedPlanner();
        var follower = new PathFollower(planner, new PathFollowConfig
        {
            AcceptRadius = 20, VerticalAcceptTolerance = 0, ReplanCooldownSeconds = 0,
            CorridorTolerance = 0, GoalRetargetTolerance = 0, ConsumePassedCollinearWaypoints = true,
        }, space);
        Vector3 goal = new(2.5f, 0, 0.5f);
        PathFollowOutput Tick(Vector3 feet, Vector3 target) => region
            ? follower.Tick(feet, new NavGoalRegion(target, 0, p => p == target), 0, 1f / 60)
            : follower.Tick(feet, target, 0, 1f / 60);
        Assert.Equal(PathFollowState.Dropping, Tick(new(0.6f, 4, 0.5f), goal).State);
        Assert.Equal(1, follower.ActiveWaypointIndex);
        var descriptor = follower.ActivePath;
        // These cover airborne XZ arrival, goal membership, corridor drift and moving goals.
        foreach (Vector3 feet in new[] { new Vector3(1.5f, 4, 0.5f), new(1.5f, 1, 0.5f), goal, new(40, 8, 40) })
        {
            var output = Tick(feet, feet);
            Assert.Equal(PathFollowState.Dropping, output.State);
            Assert.Equal(Vector2.Zero, output.WorldDir);
            var property = typeof(PathFollowOutput).GetProperty("DropTraversal");
            Assert.NotNull(property);
            var traversal = Assert.IsType<NavDropTraversal>(property.GetValue(output));
            Assert.Equal(new Vector3(0.5f, 4, 0.5f), traversal.StartFeet);
            Assert.Equal(new Vector3(1.5f, 2, 0.5f), traversal.LandingFeet);
            Assert.Equal(space.Links[0], traversal.Link);
            Assert.Same(descriptor, follower.ActivePath);
        }
        Assert.Equal(1, planner.Calls);
    }

    [Fact]
    public void CompleteDropAdvancesExactlyOneAndResetCancels()
    {
        var (space, _) = NavDropPlannerTests.Chain();
        var follower = new PathFollower(new FixedPlanner(), new PathFollowConfig { AcceptRadius = 1, ReplanCooldownSeconds = 0 }, space);
        var complete = typeof(PathFollower).GetMethod("CompleteDrop");
        Assert.NotNull(complete);
        Assert.Throws<InvalidOperationException>(() => Invoke());
        Assert.Equal(PathFollowState.Dropping, follower.Tick(new(0.5f, 4, 0.5f), new Vector3(2.5f, 0, 0.5f), 0, 0.1f).State);
        Invoke();
        Assert.Equal(2, follower.ActiveWaypointIndex);
        Assert.Throws<InvalidOperationException>(() => Invoke());
        Assert.Equal(PathFollowState.Dropping, follower.Tick(new(1.5f, 2, 0.5f), new Vector3(2.5f, 0, 0.5f), 0, 0.1f).State);
        follower.Reset();
        Assert.Null(follower.ActivePath);
        Assert.Throws<InvalidOperationException>(() => Invoke());
        void Invoke()
        {
            try { complete.Invoke(follower, null); }
            catch (System.Reflection.TargetInvocationException failure) when (failure.InnerException is not null)
            { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure.InnerException).Throw(); }
        }
    }

    [Fact]
    public void DropRequiresPhysicalSpace()
    {
        var follower = new PathFollower(new FixedPlanner(), new PathFollowConfig { AcceptRadius = 1 });
        Assert.Throws<InvalidOperationException>(() => follower.Tick(new(0.5f, 4, 0.5f), new Vector3(2.5f, 0, 0.5f), 0, 0.1f));
    }

    [Fact]
    public void TimeSpentDroppingDrainsCooldownBeforeLatestGoalReplan()
    {
        var (space, _) = NavDropPlannerTests.Chain();
        var planner = new FixedPlanner();
        var follower = new PathFollower(planner, new PathFollowConfig { AcceptRadius = 1, ReplanCooldownSeconds = 0.5f }, space);
        Assert.Equal(PathFollowState.Dropping, follower.Tick(new(0.5f, 4, 0.5f), new Vector3(2.5f, 0, 0.5f), 0, 0.1f).State);
        Vector3 latest = new(-10, 4, 0.5f);
        Assert.Equal(PathFollowState.Dropping, follower.Tick(new(1.5f, 3, 0.5f), latest, 0, 1).State);
        Assert.Equal(1, planner.Calls);
        follower.CompleteDrop();
        follower.Tick(new(1.5f, 2, 0.5f), latest, 0, 0.1f);
        Assert.Equal(2, planner.Calls);
        Assert.Equal(latest, planner.LastGoal);
    }

    sealed class FixedPlanner : IRegionPathPlanner
    {
        internal int Calls;
        internal Vector3 LastGoal;
        NavPath Plan()
        {
            Calls++;
            return new(NavPathStatus.Complete, new[] { new NavWaypoint(new(0.5f, 0.5f), 0),
                new NavWaypoint(new(1.5f, 0.5f), 0) { Kind = NavWaypointKind.Drop },
                new NavWaypoint(new(2.5f, 0.5f), 0) { Kind = NavWaypointKind.Drop } });
        }
        public NavPath FindPath(Vector3 start, Vector3 goal, float radius, PathQueryBudget budget)
        { LastGoal = goal; return Plan(); }
        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float radius, PathQueryBudget budget) => Plan();
    }
}
