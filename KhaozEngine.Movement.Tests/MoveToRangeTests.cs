using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class MoveToRangeTests
{
    internal static readonly MoveTuning Tuning = GroundTraversalProbeTests.Tuning with { WalkSpeed = 2f, RunSpeed = 5f };
    internal static readonly NavSpace Space = NavSpace.Single(NavGrid.FromWalkable(32, 32, 0.25f, -4f, -4f, (_, _) => true));
    internal static readonly GroundMoveContext Flat = new((_, _) => 0f);
    internal static MoveState Body(float x = 0f, float z = 0f) => new() { Position = new(x, 0.75f, z), Grounded = true };
    internal static MovementBody Shape(in MoveState body, in MoveTuning tuning)
        => new(body.Position, tuning.CapsuleRadius, tuning.CapsuleHalfHeight);
    internal static Vector3 Feet(in MoveState body, in MoveTuning tuning)
        => body.Position - new Vector3(0f, tuning.CapsuleHalfHeight, 0f);
    internal static NavPath Route(NavPathStatus status, params Vector2[] points)
        => new(status, Array.ConvertAll(points, point => new NavWaypoint(point, 0)));

    [Fact]
    public void ArrivalUsesOnlyObservedShapeReach()
    {
        var planner = new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(2f, 0f)));
        var mover = new MoveToRange(planner, Space, (_, _) => true);
        MoveState body = Body();
        ReachTarget target = ReachTarget.Point(new(2f, 0.75f, 0f));
        RangeSteering outside = mover.Tick(body, Tuning, target, 0.6f, true, 0.2f, Flat);
        Assert.Equal(RangeMoveStatus.Following, outside.Status);
        Assert.InRange(outside.WorldDirection.Length(), 0f, 1f);
        Assert.Equal(Body(), body);
        body = Flat.Step(body, outside.WorldDirection, true, 0.2f, Tuning);
        Assert.Equal(1f, body.Position.X, 6);
        Assert.Equal(RangeMoveStatus.Following, mover.Tick(body, Tuning, target, 0.6f, true, 0.2f, Flat).Status);
        RangeSteering final = mover.Tick(body, Tuning, target, 0.6f, true, 0.2f, Flat);
        body = Flat.Step(body, final.WorldDirection, true, 0.2f, Tuning);
        Assert.InRange(body.Position.X, 1.2f, 1.200001f);
        Assert.Equal(RangeMoveStatus.InRange, mover.Tick(body, Tuning, target, 0.6f, true, 0.2f, Flat).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SuspensionPrecedesReachAndPreservesTheSuppliedState(bool commitment)
    {
        var planner = new ScriptPlanner((_, _) => throw new InvalidOperationException("Suspended bodies cannot plan."));
        var mover = new MoveToRange(planner, Space, (_, _) => true);
        MoveState body = Body();
        body.Grounded = commitment;
        body.VerticalVelocity = 3f;
        body.HorizontalVelocity = new Vector2(2f, 4f);
        body.Commitment = commitment ? new MovementCommitment { Phase = MovementCommitmentPhase.Recovering } : default;
        MoveState original = body;
        RangeSteering steering = mover.Tick(body, Tuning, ReachTarget.Point(body.Position), 0f, true, 0.1f, Flat);
        Assert.Equal(RangeMoveStatus.Suspended, steering.Status);
        Assert.Equal(Vector2.Zero, steering.WorldDirection);
        Assert.Equal(original, body);
    }

    [Fact]
    public void DifferentHeightAndHalfHeightDoNotProduceFalseArrival()
    {
        var planner = new ScriptPlanner((start, goal) =>
        {
            Assert.Equal(Vector3.Zero, start);
            Assert.False(goal.Contains(Vector3.Zero));
            Assert.False(goal.Contains(new Vector3(0f, 0.5f, 0f)));
            return NavPath.Unreachable;
        });
        var mover = new MoveToRange(planner, Space, (_, _) => true);
        ReachTarget target = ReachTarget.Capsule(new MovementBody(new(0f, 4f, 0f), 0.3f, 1.5f));
        MoveState body = Body();
        RangeSteering result = mover.Tick(body, Tuning, target, 0.2f, false, 0.1f, Flat);
        Assert.Equal(RangeMoveStatus.Unreachable, result.Status);
        Assert.Equal(Vector2.Zero, result.WorldDirection);
        Assert.Equal(0.75f, body.Position.Y);
    }

    [Fact]
    public void RegionSeatsEachCandidateUsingTheMoversOwnHalfHeight()
    {
        var planner = new ScriptPlanner((start, goal) =>
        {
            Assert.Equal(Vector3.Zero, start);
            Assert.True(goal.Contains(new Vector3(2f, 0f, 0f)));
            Assert.False(goal.Contains(new Vector3(2f, -2f, 0f)));
            return Route(NavPathStatus.Complete, new Vector2(2f, 0f));
        });
        var mover = new MoveToRange(planner, Space, (_, _) => true);
        ReachTarget target = ReachTarget.Capsule(new MovementBody(new(2f, 1.5f, 0f), 0.2f, 1.5f));
        Assert.Equal(RangeMoveStatus.Following, mover.Tick(Body(), Tuning, target, 0f, false, 0.1f, Flat).Status);
        Assert.Equal(1, planner.Queries);
    }

    [Fact]
    public void FinalFractionBelowTheLegacyDeadZoneStillReachesTheNominalRing()
    {
        var mover = new MoveToRange(new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(0.8001f, 0f))), Space, (_, _) => true);
        MoveState body = Body();
        ReachTarget target = ReachTarget.Point(new(0.8001f, 0.75f, 0f));
        RangeSteering steering = mover.Tick(body, Tuning, target, 0.6f, true, 1f / 30f, Flat);
        Assert.Equal(RangeMoveStatus.Following, steering.Status);
        Assert.InRange(steering.WorldDirection.Length(), 0.000001f, 0.001f);
        MoveState moved = Flat.Step(body, steering.WorldDirection, true, 1f / 30f, Tuning);
        Assert.InRange(moved.Position.X, 0.000099f, 0.000102f);
        Assert.True(ReachGeometry.Within(Shape(moved, Tuning), target, 0.6f));
        Assert.Equal(RangeMoveStatus.InRange, mover.Tick(moved, Tuning, target, 0.6f, true, 1f / 30f, Flat).Status);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.7f)]
    public void BoxApproachStopsAtTheActualFaceRatherThanItsBoundingCircle(float yaw)
    {
        ReachTarget target = ReachTarget.Box(new(2f, 0.75f, 0f), new(0.5f, 0.75f, 0.2f), yaw);
        var mover = new MoveToRange(new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(2f, 0f))), Space, (_, _) => true);
        MoveState body = Body();
        for (int tick = 0; tick < 30; tick++)
        {
            RangeSteering steering = mover.Tick(body, Tuning, target, 0.4f, true, 0.1f, Flat);
            if (steering.Status == RangeMoveStatus.InRange) break;
            body = Flat.Step(body, steering.WorldDirection, true, 0.1f, Tuning);
        }
        Assert.True(ReachGeometry.Within(Shape(body, Tuning), target, 0.4f));
        Assert.InRange(ReachGeometry.Distance(Shape(body, Tuning), target), 0.39999f, 0.4f);
        Assert.True(body.Position.X > 0.8f);
    }

    [Fact]
    public void SupportHeightIsPredictedBeforeTheFinalIntersection()
    {
        var context = new GroundMoveContext((x, _) => x * 0.1f);
        ReachTarget target = ReachTarget.Point(new(0.85f, 1.5f, 0f));
        var mover = new MoveToRange(new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(0.85f, 0f))), Space, (_, _) => true);
        MoveState body = Body();
        for (int tick = 0; tick < 16; tick++)
        {
            RangeSteering steering = mover.Tick(body, Tuning, target, 0.4f, true, 0.1f, context);
            if (steering.Status == RangeMoveStatus.InRange) break;
            body = context.Step(body, steering.WorldDirection, true, 0.1f, Tuning);
        }
        Assert.True(ReachGeometry.Within(Shape(body, Tuning), target, 0.4f));
        Assert.InRange(ReachGeometry.Distance(Shape(body, Tuning), target), 0.39999f, 0.4f);
        Assert.Equal(0.75f + body.Position.X * 0.1f, body.Position.Y, 6);
    }

    [Fact]
    public void HopRouteIsUnsupportedAndZeroIsARealWaypoint()
    {
        var hop = new MoveToRange(new ScriptPlanner((_, _) => new NavPath(NavPathStatus.Complete,
            [new NavWaypoint(Vector2.UnitX, 0) { Kind = NavWaypointKind.Hop }])), Space, (_, _) => true);
        ReachTarget target = ReachTarget.Point(new(2f, 0.75f, 0f));
        RangeSteering unsupported = hop.Tick(Body(), Tuning, target, 0.1f, false, 0.1f, Flat);
        Assert.Equal(RangeMoveStatus.UnsupportedTransition, unsupported.Status);
        Assert.Equal(Vector2.Zero, unsupported.WorldDirection);
        var zero = new MoveToRange(new ScriptPlanner((_, _) => Route(NavPathStatus.Partial, Vector2.Zero)), Space, (_, _) => true);
        MoveState body = Body(-0.2f);
        RangeSteering direction = zero.Tick(body, Tuning, target, 0.1f, false, 0.1f, Flat);
        Assert.Equal(RangeMoveStatus.Following, direction.Status);
        body = Flat.Step(body, direction.WorldDirection, false, 0.1f, Tuning);
        Assert.Equal(0f, body.Position.X, 6);
        RangeSteering wait = zero.Tick(body, Tuning, target, 0.1f, false, 0.1f, Flat);
        Assert.Equal(RangeMoveStatus.WaitingForPath, wait.Status);
        Assert.Equal(Vector2.Zero, wait.WorldDirection);
    }

    [Fact]
    public void TranslationUsesFollowerDriftAndCooldownButResetPlansFresh()
    {
        var planner = new ScriptPlanner((_, goal) => Route(NavPathStatus.Complete, new Vector2(goal.Anchor.X, goal.Anchor.Z)));
        var mover = new MoveToRange(planner, Space, (_, _) => true, new PathFollowConfig { ReplanCooldownSeconds = 1f, GoalRetargetTolerance = 0.5f });
        MoveState body = Body();
        Assert.Equal(RangeMoveStatus.Following, mover.Tick(body, Tuning, ReachTarget.Point(new(2f, 0.75f, 0f)), 0.1f, false, 0.1f, Flat).Status);
        mover.Tick(body, Tuning, ReachTarget.Point(new(2.2f, 0.75f, 0f)), 0.1f, false, 0.1f, Flat);
        mover.Tick(body, Tuning, ReachTarget.Point(new(3f, 0.75f, 0f)), 0.1f, false, 0.1f, Flat);
        Assert.Equal(1, planner.Queries);
        mover.Tick(body, Tuning, ReachTarget.Point(new(3f, 0.75f, 0f)), 0.1f, false, 1f, Flat);
        Assert.Equal(2, planner.Queries);
        mover.Reset();
        mover.Tick(body, Tuning, ReachTarget.Point(new(1f, 0.75f, 0f)), 0.1f, false, 0.01f, Flat);
        Assert.Equal(3, planner.Queries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ShapeDimensionsYawAndRangeChangesResetCooldown(int change)
    {
        var planner = new ScriptPlanner((_, _) => Route(NavPathStatus.Complete, new Vector2(3f, 0f)));
        var mover = new MoveToRange(planner, Space, (_, _) => true, new PathFollowConfig { ReplanCooldownSeconds = 10f });
        ReachTarget first = ReachTarget.Box(new(3f, 0.75f, 0f), new(0.5f, 0.5f, 0.5f));
        mover.Tick(Body(), Tuning, first, 0.1f, false, 0.1f, Flat);
        ReachTarget next = change switch
        {
            0 => ReachTarget.Point(new(3f, 0.75f, 0f)),
            1 => ReachTarget.Box(new(3f, 0.75f, 0f), new(0.6f, 0.5f, 0.5f)),
            2 => ReachTarget.Box(new(3f, 0.75f, 0f), new(0.5f, 0.5f, 0.5f), 0.1f),
            _ => first,
        };
        mover.Tick(Body(), Tuning, next, change == 3 ? 0.2f : 0.1f, false, 0.01f, Flat);
        Assert.Equal(2, planner.Queries);
    }

    [Theory]
    [InlineData(float.NaN, 0.1f)]
    [InlineData(-1f, 0.1f)]
    [InlineData(1f, float.NaN)]
    [InlineData(1f, 0f)]
    public void InvalidRangeAndDtAreRefused(float range, float dt)
    {
        var mover = new MoveToRange(new ScriptPlanner((_, _) => NavPath.Unreachable), Space, (_, _) => true);
        Assert.Throws<ArgumentOutOfRangeException>(() => { mover.Tick(Body(), Tuning, ReachTarget.Point(Vector3.Zero), range, false, dt, Flat); });
    }

    [Fact]
    public void InvalidBodyTargetAndTuningAreRefusedBeforePlanning()
    {
        var mover = new MoveToRange(new ScriptPlanner((_, _) => throw new InvalidOperationException()), Space, (_, _) => true);
        MoveState invalid = Body();
        invalid.Position.X = float.NaN;
        Assert.Throws<ArgumentOutOfRangeException>(() => { mover.Tick(invalid, Tuning, ReachTarget.Point(Vector3.Zero), 0f, false, 0.1f, Flat); });
        Assert.Throws<ArgumentException>(() => { mover.Tick(Body(), Tuning, default, 0f, false, 0.1f, Flat); });
        Assert.Throws<ArgumentOutOfRangeException>(() => { mover.Tick(Body(), Tuning with { RunSpeed = float.PositiveInfinity }, ReachTarget.Point(Vector3.Zero), 0f, false, 0.1f, Flat); });
    }

    internal sealed class ScriptPlanner(Func<Vector3, NavGoalRegion, NavPath> find) : IRegionPathPlanner
    {
        public int Queries { get; private set; }
        public NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)
        {
            Queries++;
            return find(start, goal);
        }
        public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget)
            => throw new InvalidOperationException("Range steering must use region queries.");
    }
}
