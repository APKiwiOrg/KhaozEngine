using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.GroundPursuitFixture;

namespace KhaozEngine.Tests.Movement;

public class GroundPursuitTests(GroundPursuitFixture fixture) : IClassFixture<GroundPursuitFixture>
{
    private static GroundPursuitSettings Limits => new(5, 20, 0.1f);

    [Theory]
    [InlineData(2, 5, GroundPursuitStatus.OutOfRange)]
    [InlineData(4, 2, GroundPursuitStatus.TooLong)]
    [InlineData(4, 5, GroundPursuitStatus.Ready)]
    public void TriggerAndHorizontalPathCapAreIndependent(float trigger, float cap, GroundPursuitStatus expected)
    {
        var scene = fixture.Flat;
        var pursuit = new GroundPursuit(scene.Navigation);
        var body = scene.Body();
        var original = body;
        GroundPursuitResult result = pursuit.Admit(body, Tuning, scene.Target(), new(trigger, cap, 0.1f), scene.Context);
        Assert.Equal(expected, result.Status);
        Assert.Equal(original, body);
        if (expected == GroundPursuitStatus.Ready)
        {
            AssertCommand(result, positive: true);
            Assert.Equal(3f, result.RemainingPathLength, 5);
        }
    }

    [Fact]
    public void DropEnabledProfileUsesWalkingDetourAtAdmissionAndAfterTargetMotion()
    {
        var scene = fixture.Detour;
        var body = scene.Body(surface: 0.9f);
        Vector3 feet = body.Position - Vector3.UnitY * Tuning.CapsuleHalfHeight;
        var target = scene.Target();
        NavPath ordinary = scene.Navigation.Planner.FindPath(feet,
            scene.Body(1.75f).Position - Vector3.UnitY * Tuning.CapsuleHalfHeight,
            Tuning.CapsuleRadius, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, ordinary.Status);
        Assert.Contains(ordinary.Waypoints, w => w.Kind == NavWaypointKind.Drop);
        var pursuit = new GroundPursuit(scene.Navigation);
        var ready = pursuit.Admit(body, Tuning, target, Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, ready.Status);
        AssertCommand(ready, positive: true);
        Assert.True(ready.RemainingPathLength > 3);
        Assert.True(ready.Direction.Y > 0);
        var moving = pursuit.Tick(body, Tuning, scene.Target(2.75f), 1, scene.Context);
        Assert.Equal(GroundPursuitStatus.Travelling, moving.Status);
        AssertCommand(moving, positive: true);
        Assert.True(moving.Direction.Y > 0);
        var capped = new GroundPursuit(scene.Navigation).Admit(body, Tuning, target, new(5, 3, 0.1f), scene.Context);
        Assert.Equal(GroundPursuitStatus.TooLong, capped.Status);
    }

    [Fact]
    public void StepRiseDoesNotSpendHorizontalPathBudget()
    {
        var scene = fixture.Detour;
        var result = new GroundPursuit(scene.Navigation).Admit(scene.Body(0.25f, 2.75f, 0.6f), Tuning,
            scene.Target(2.75f, 2.75f), new(5, 2.5f, 0.1f), scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, result.Status);
        AssertCommand(result, positive: true);
        Assert.Equal(2.5f, result.RemainingPathLength, 5);
    }

    [Fact]
    public void VerticalOnlyRouteHasNoUsableCommitmentDirection()
    {
        var scene = fixture.Detour;
        var body = fixture.Flat.Body();
        var destination = scene.Body(surface: 0.9f);
        int layer = scene.Navigation.Space.LayerAt(destination.Position - Vector3.UnitY * Tuning.CapsuleHalfHeight);
        var path = new NavPath(NavPathStatus.Complete, [new(new(body.Position.X, body.Position.Z), layer)]);
        // A third-party planner claims this vertical edge is Walk. Even then the result cannot be forwarded.
        var pursuit = Fixed(scene, path, (_, _) => true);
        var result = pursuit.Admit(body, Tuning, ReachTarget.Point(destination.Position), new(5, 5, 0.1f), scene.Context);
        Assert.Equal(GroundPursuitStatus.NoPath, result.Status);
        Assert.Equal(GroundPursuitDetail.NoDirection, result.Detail);
        body.Position.X -= 0.5f;
        Assert.Equal(GroundPursuitStatus.Ready, pursuit.Admit(body, Tuning, ReachTarget.Point(destination.Position),
            new(5, 5, 0.1f), scene.Context).Status);
    }

    [Fact]
    public void DropWithoutAWalkingRouteRefusesRatherThanFalling()
    {
        var scene = fixture.Cliff;
        var result = new GroundPursuit(scene.Navigation).Admit(scene.Body(surface: 0.9f), Tuning,
            scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.NoPath, result.Status);
        Assert.Contains(result.Detail, new[] { GroundPursuitDetail.PartialRoute, GroundPursuitDetail.UnreachableRoute });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExhaustedSearchBudgetRefusesPartialOrUnreachableRoutes(bool noProgress)
    {
        var scene = fixture.Flat;
        var config = new PathFollowConfig { Budget = PathQueryBudget.Default with { MaxExpandedNodes = noProgress ? 0 : 2 } };
        var result = new GroundPursuit(scene.Navigation, config).Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.NoPath, result.Status);
        Assert.Equal(noProgress ? GroundPursuitDetail.UnreachableRoute : GroundPursuitDetail.PartialRoute, result.Detail);
        Assert.Equal(GroundPursuitStatus.Ready,
            new GroundPursuit(scene.Navigation).Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context).Status);
    }

    [Theory]
    [InlineData(NavWaypointKind.Walk, GroundPursuitStatus.Ready)]
    [InlineData(NavWaypointKind.Hop, GroundPursuitStatus.NoPath)]
    [InlineData(NavWaypointKind.Drop, GroundPursuitStatus.NoPath)]
    [InlineData((NavWaypointKind)99, GroundPursuitStatus.NoPath)]
    public void PlannerIgnoringWalkOnlyCannotAdmitSpecialOrUnknownWaypoints(NavWaypointKind kind, GroundPursuitStatus expected)
    {
        var scene = fixture.Flat;
        var path = new NavPath(NavPathStatus.Complete, [new(new(1.75f, 0.25f), 0) { Kind = kind }]);
        var result = Fixed(scene, path).Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(expected, result.Status);
        Assert.Equal(kind == NavWaypointKind.Walk ? GroundPursuitDetail.None : GroundPursuitDetail.NonWalkRoute, result.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidEndpointOrActualEntryHasATypedRefusal(bool entry)
    {
        var scene = fixture.Flat;
        var valid = new NavPath(NavPathStatus.Complete, [new(new(1.75f, 0.25f), 0)]);
        Assert.Equal(GroundPursuitStatus.Ready, Fixed(scene, valid).Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context).Status);
        var invalid = entry ? valid : new NavPath(NavPathStatus.Complete, [new(new(0.25f, 0.25f), 0)]);
        var pursuit = Fixed(scene, invalid, entry ? (_, _) => false : null);
        var result = pursuit.Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.NoPath, result.Status);
        Assert.Equal(entry ? GroundPursuitDetail.InvalidEntry : GroundPursuitDetail.InvalidEndpoint, result.Detail);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MalformedPlannerWaypointRefusesBeforeShortcutProcessing(bool invalidLayer)
    {
        var scene = fixture.Flat;
        var valid = new NavWaypoint(new(1.75f, 0.25f), 0);
        Assert.Equal(GroundPursuitStatus.Ready,
            Fixed(scene, new(NavPathStatus.Complete, [valid])).Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context).Status);
        var broken = invalidLayer ? valid with { Layer = 999 } : valid with { Position = new(float.NaN, 0.25f) };
        var result = Fixed(scene, new(NavPathStatus.Complete, [broken, valid]))
            .Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.NoPath, result.Status);
        Assert.Equal(GroundPursuitDetail.InvalidEndpoint, result.Detail);
    }

    [Fact]
    public void EmptyCompleteRouteCannotProduceAReadyRequest()
    {
        var scene = fixture.Flat;
        Assert.Equal(GroundPursuitStatus.Ready,
            Fixed(scene, new(NavPathStatus.Complete, [new(new(1.75f, 0.25f), 0)]))
                .Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context).Status);
        var result = Fixed(scene, new(NavPathStatus.Complete, [])).Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.NoPath, result.Status);
        Assert.Equal(GroundPursuitDetail.EmptyRoute, result.Detail);
    }

    [Fact]
    public void AdmissionAndArrivalWitnessCurrentExactThreeDimensionalReach()
    {
        var scene = fixture.Flat;
        var body = scene.Body();
        var pursuit = new GroundPursuit(scene.Navigation);
        var touching = ReachTarget.Capsule(new(body.Position, 0.3f, 1.5f));
        Assert.Equal(GroundPursuitStatus.AlreadyInRange, pursuit.Admit(body, Tuning, touching, new(0.01f, 1, 0), scene.Context).Status);
        var elevated = ReachTarget.Capsule(new(body.Position + Vector3.UnitY * 5, 0.3f, 1.5f));
        var unreachable = pursuit.Admit(body, Tuning, elevated, new(10, 20, 0.1f), scene.Context);
        Assert.Equal(GroundPursuitStatus.NoPath, unreachable.Status);
        Assert.Equal(GroundPursuitStatus.Ready, pursuit.Admit(body, Tuning, scene.Target(), Limits, scene.Context).Status);
        // A carried budget reaching zero is not arrival. Only the supplied current target and capsule are.
        body.Commitment = MovementCommitment.ForGroundTravel(1, Vector2.UnitX, 5, 1) with
        { Phase = MovementCommitmentPhase.Travelling, DistanceRemaining = 0 };
        var moving = pursuit.Tick(body, Tuning, scene.Target(), 0.1f, scene.Context);
        Assert.Equal(GroundPursuitStatus.Travelling, moving.Status);
        AssertCommand(moving, positive: true);
        Assert.Equal(GroundPursuitStatus.Arrived, pursuit.Tick(body, Tuning, touching, 0.1f, scene.Context).Status);
    }

    [Fact]
    public void ValidFinalApproachKeepsSteeringThroughPreparationUntilExactReach()
    {
        var scene = fixture.Flat;
        const float dt = 0.1f;
        var pursuit = new GroundPursuit(scene.Navigation, new PathFollowConfig { ReplanCooldownSeconds = 10 });
        var body = scene.Body(1.25f);
        var target = scene.Target();
        var ready = pursuit.Admit(body, Tuning, target, Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, ready.Status);
        Assert.True(ready.RemainingPathLength < PathFollowConfig.Default.AcceptRadius);
        body.Commitment = MovementCommitment.ForGroundTravel(1, ready.Direction, 0.5f, ready.RemainingPathLength,
            preparationSeconds: 0.3f, timeoutSeconds: 3);
        bool prepared = false, approached = false, arrived = false;
        for (int tick = 0; tick < 40; tick++)
        {
            bool within = ReachGeometry.Within(new(body.Position, Tuning.CapsuleRadius, Tuning.CapsuleHalfHeight), target, Limits.StopDistance);
            var update = pursuit.Tick(body, Tuning, target, dt, scene.Context);
            Assert.Equal(within ? GroundPursuitStatus.Arrived : GroundPursuitStatus.Travelling, update.Status);
            if (within) { arrived = true; break; }
            AssertCommand(update, positive: true);
            prepared |= body.Commitment.Phase == MovementCommitmentPhase.Preparing;
            approached |= body.Commitment.Phase == MovementCommitmentPhase.Travelling;
            body.Commitment = GroundTravelCommitment.Steer(body.Commitment, 1, update.Direction, update.RemainingPathLength);
            body = scene.Context.Step(body, Vector2.Zero, false, dt, Tuning);
            Assert.True(body.Commitment.IsActive);
        }
        Assert.True(prepared);
        Assert.True(approached);
        Assert.True(arrived);
    }

    [Fact]
    public void NoDirectionAtValidEndpointHoldsForARefreshRatherThanEndingPursuit()
    {
        var scene = fixture.Detour;
        var body = scene.Body(2.75f, 2.75f);
        var endpoint = scene.Body(2.25f, 2.75f);
        var nextEndpoint = scene.Body(1.75f, 2.75f, 0.3f);
        Vector3 Feet(MoveState state) => state.Position - Vector3.UnitY * Tuning.CapsuleHalfHeight;
        NavWaypoint Waypoint(MoveState state) => new(new(state.Position.X, state.Position.Z), scene.Navigation.Space.LayerAt(Feet(state)));
        var target = ReachTarget.Point(new(2.25f, 2.5f, 2.75f));
        var limits = new GroundPursuitSettings(5, 5, 1);
        bool refreshed = false;
        var planner = new MoveToRangeTests.ScriptPlanner((_, _) => new(NavPathStatus.Complete,
            [Waypoint(refreshed ? nextEndpoint : endpoint)]));
        var pursuit = new GroundPursuit(planner, scene.Navigation.Space, scene.Navigation.AllowsSegment,
            new PathFollowConfig { ReplanCooldownSeconds = 10 });
        var ready = pursuit.Admit(body, Tuning, target, limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, ready.Status);
        Assert.True(ReachGeometry.Within(new(endpoint.Position, Tuning.CapsuleRadius, Tuning.CapsuleHalfHeight), target, limits.StopDistance));
        body = endpoint;
        // The authoritative centre is just below exact reach, although the captured endpoint remains valid.
        body.Position.Y = MathF.BitDecrement(0.75f);
        Assert.False(ReachGeometry.Within(new(body.Position, Tuning.CapsuleRadius, Tuning.CapsuleHalfHeight), target, limits.StopDistance));
        Assert.True(scene.Navigation.AllowsSegment(Feet(body), Feet(endpoint)));
        var held = pursuit.Tick(body, Tuning, target, 0.01f, scene.Context);
        Assert.Equal(GroundPursuitStatus.Travelling, held.Status);
        Assert.Equal(GroundPursuitDetail.None, held.Detail);
        Assert.Equal(ready.Direction, held.Direction);
        Assert.Equal(0, held.RemainingPathLength);
        refreshed = true;
        var update = pursuit.Tick(body, Tuning, target, 10, scene.Context);
        Assert.Equal(GroundPursuitStatus.Travelling, update.Status);
        AssertCommand(update, positive: true);
        Assert.Equal(0.5f, update.RemainingPathLength, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GroundCommitmentCrossesRetainedCornerWithoutReturningAcrossIt(bool refuseCrossedShortcut)
    {
        var scene = fixture.Flat;
        const float dt = 0.1f, speed = 15;
        var body = scene.Body(-0.5f);
        Vector2 corner = new(0.25f, 0.25f), end = new(0.25f, 3.25f);
        var target = scene.Target(end.X, end.Y);
        bool blocked = false;
        bool Allows(Vector3 from, Vector3 to) => scene.Navigation.AllowsSegment(from, to)
            && !(from.X < corner.X && to.Z > corner.Y)
            && !(blocked && to.Z > corner.Y);
        var path = new NavPath(NavPathStatus.Complete, [new(corner, 0), new(end, 0)]);
        var pursuit = Fixed(scene, path, Allows);
        var ready = pursuit.Admit(body, Tuning, target, Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, ready.Status);
        Assert.Equal(Vector2.UnitX, ready.Direction); // The corner survived straightening.
        Assert.True(speed * dt > 2 * PathFollowConfig.Default.AcceptRadius);
        body.Commitment = MovementCommitment.ForGroundTravel(1, ready.Direction, speed, ready.RemainingPathLength, timeoutSeconds: 3);
        bool crossed = false, resumed = false, arrived = false;
        for (int tick = 0; tick < 12; tick++)
        {
            var update = pursuit.Tick(body, Tuning, target, dt, scene.Context);
            if (update.Status == GroundPursuitStatus.Arrived) { arrived = true; break; }
            Assert.Equal(GroundPursuitStatus.Travelling, update.Status);
            if (blocked)
            {
                // Returning to the old corner remains legal, but the crossed entry to the next leg is refused.
                Assert.True(Allows(body.Position - Vector3.UnitY * Tuning.CapsuleHalfHeight,
                    new(corner.X, body.Position.Y - Tuning.CapsuleHalfHeight, corner.Y)));
                Assert.Equal(0, update.RemainingPathLength);
                Assert.Equal(ready.Direction, update.Direction);
            }
            else
            {
                AssertCommand(update, positive: true);
                if (crossed)
                {
                    Assert.True(update.Direction.Y > 0);
                    resumed = true;
                }
            }
            Vector3 before = body.Position;
            body.Commitment = GroundTravelCommitment.Steer(body.Commitment, 1, update.Direction, update.RemainingPathLength);
            body = scene.Context.Step(body, Vector2.Zero, false, dt, Tuning);
            Assert.True(body.Commitment.IsActive);
            if (crossed) Assert.True(body.Position.Z >= before.Z);
            if (blocked)
            {
                Assert.Equal(new Vector2(before.X, before.Z), new Vector2(body.Position.X, body.Position.Z));
                blocked = false;
            }
            if (!crossed && body.Position.X > corner.X + PathFollowConfig.Default.AcceptRadius)
            {
                Assert.Equal(corner.Y, body.Position.Z, 5);
                crossed = true;
                blocked = refuseCrossedShortcut;
            }
        }
        Assert.True(crossed);
        Assert.True(resumed);
        Assert.True(arrived);
        Assert.True(ReachGeometry.Within(new(body.Position, Tuning.CapsuleRadius, Tuning.CapsuleHalfHeight), target, Limits.StopDistance));
    }

    [Theory]
    [InlineData(0.05f)]
    [InlineData(null)]
    public void GroundCommitmentCrossesLayeredDetourWithoutReversing(float? acceptRadius)
    {
        var scene = fixture.Detour;
        const float dt = 1f / 30, speed = 12;
        var body = scene.Body(surface: 0.9f);
        var target = scene.Target();
        var pursuit = new GroundPursuit(scene.Navigation, new PathFollowConfig
        { AcceptRadius = acceptRadius ?? PathFollowConfig.Default.AcceptRadius });
        var ready = pursuit.Admit(body, Tuning, target, Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, ready.Status);
        body.Commitment = MovementCommitment.ForGroundTravel(1, ready.Direction, speed, ready.RemainingPathLength, timeoutSeconds: 10);
        Vector2? previousDirection = null;
        int? previousLayer = null;
        bool changedLayer = false, arrived = false;
        for (int tick = 0; tick < 100; tick++)
        {
            Vector3 feet = body.Position - Vector3.UnitY * Tuning.CapsuleHalfHeight;
            int layer = scene.Navigation.Space.LayerAt(feet);
            var update = pursuit.Tick(body, Tuning, target, dt, scene.Context);
            if (update.Status == GroundPursuitStatus.Arrived) { arrived = true; break; }
            Assert.Equal(GroundPursuitStatus.Travelling, update.Status);
            AssertCommand(update, positive: false);
            if (previousLayer is int before && before != layer)
            {
                changedLayer = true;
                // Crossing a supported layer boundary must not reverse the previous steering basis.
                Assert.True(Vector2.Dot(previousDirection!.Value, update.Direction) > -0.9999f);
            }
            previousDirection = update.Direction;
            previousLayer = layer;
            body.Commitment = GroundTravelCommitment.Steer(body.Commitment, 1, update.Direction, update.RemainingPathLength);
            body = scene.Context.Step(body, Vector2.Zero, false, dt, Tuning);
            Assert.True(body.Commitment.IsActive);
        }
        Assert.True(changedLayer);
        Assert.True(arrived);
        Assert.True(ReachGeometry.Within(new(body.Position, Tuning.CapsuleRadius, Tuning.CapsuleHalfHeight), target, Limits.StopDistance));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    public void LayerChangesAdvanceOnlyAfterCrossingOrMakeReplanningDue(int actorLayer, bool crossed)
    {
        var scene = fixture.Flat;
        float[] heights = [0, 0.3f, -0.3f];
        var space = new NavSpace(heights.Select(height => NavGrid.FromBlockedSurfaces(8, 8, 0.5f, -2, -2,
            new bool[64], Enumerable.Repeat(height, 64).ToArray())).ToArray());
        var path = new NavPath(NavPathStatus.Complete,
            [new(new(0.25f, 0.25f), 0), new(new(0.25f, 1.75f), 1)]);
        var planner = new MoveToRangeTests.ScriptPlanner((start, _) => start.Y < -0.1f ? NavPath.Unreachable : path);
        // Isolate layer policy with admissible entries, including the segment back to the old waypoint.
        var pursuit = new GroundPursuit(planner, space, (_, _) => true,
            new PathFollowConfig { ReplanCooldownSeconds = 10 });
        var body = scene.Body(-0.75f);
        var target = ReachTarget.Point(new(0.25f, heights[1] + Tuning.CapsuleHalfHeight, 1.75f));
        Assert.Equal(GroundPursuitStatus.Ready, pursuit.Admit(body, Tuning, target, Limits, scene.Context).Status);
        body.Position = new(crossed ? 1 : -0.125f, heights[actorLayer] + Tuning.CapsuleHalfHeight, 0.25f);
        Assert.Equal(actorLayer, space.LayerAt(body.Position - Vector3.UnitY * Tuning.CapsuleHalfHeight));
        var update = pursuit.Tick(body, Tuning, target, 0.01f, scene.Context);
        Assert.Equal(GroundPursuitStatus.Travelling, update.Status);
        if (crossed && actorLayer == 2)
        {
            Assert.Equal(0, update.RemainingPathLength);
            Assert.Equal(Vector2.UnitX, update.Direction);
            var refreshed = pursuit.Tick(body, Tuning, target, 10, scene.Context);
            Assert.Equal(GroundPursuitStatus.NoPath, refreshed.Status);
            Assert.Equal(GroundPursuitDetail.UnreachableRoute, refreshed.Detail);
        }
        else
        {
            AssertCommand(update, positive: true);
            if (crossed) Assert.True(update.Direction.Y > 0);
            else
            {
                Assert.Equal(Vector2.UnitX, update.Direction);
                Assert.Equal(1.875f, update.RemainingPathLength);
            }
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void StationaryActorApproachesItsNewLegAfterProximitySkip(bool skipNearbyWaypoint, bool freshReplan)
    {
        var scene = fixture.Flat;
        var body = scene.Body(-0.3125f);
        Vector2 first = new(0.25f, 0.25f), second = new(-0.25f, 0.25f), end = new(-0.25f, 3.25f);
        var target = scene.Target(end.X, end.Y);
        // N1. Centre-line admission keeps the raw turn through straightening, permits start-to-second
        // entry and refuses start-to-final entry. Offset lines cannot remove the nearby first waypoint.
        bool Allows(Vector3 from, Vector3 to) => scene.Navigation.AllowsSegment(from, to)
            && (from.Z == first.Y && to.Z == first.Y || from.X == second.X && to.X == second.X);
        var route = new NavPath(NavPathStatus.Complete, skipNearbyWaypoint
            ? [new(first, 0), new(second, 0), new(end, 0)]
            : [new(second, 0), new(end, 0)]);
        NavPath planned = freshReplan ? new(NavPathStatus.Complete, [new(new(1.25f, 0.25f), 0)]) : route;
        var planner = new MoveToRangeTests.ScriptPlanner((_, _) => planned);
        var pursuit = new GroundPursuit(planner, scene.Navigation.Space, Allows,
            new PathFollowConfig { ReplanCooldownSeconds = 10 });
        var admitted = pursuit.Admit(body, Tuning, freshReplan ? scene.Target(1.25f) : target, Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, admitted.Status);
        if (freshReplan)
        {
            planned = route;
            var refreshed = pursuit.Tick(body, Tuning, target, 10, scene.Context);
            Assert.Equal(GroundPursuitStatus.Travelling, refreshed.Status);
            Assert.Equal(3.0625f, refreshed.RemainingPathLength);
            Assert.Equal(Vector2.UnitX, refreshed.Direction);
        }
        else Assert.Equal(3.0625f, admitted.RemainingPathLength);
        var stationary = pursuit.Tick(body, Tuning, target, 0.01f, scene.Context);
        Assert.Equal(GroundPursuitStatus.Travelling, stationary.Status);
        Assert.Equal(GroundPursuitDetail.None, stationary.Detail);
        Assert.Equal(3.0625f, stationary.RemainingPathLength);
        Assert.Equal(Vector2.UnitX, stationary.Direction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidatedEndpointHoldsDuringCooldownThenReplansEvenBelowDriftTolerance(bool consumed)
    {
        var scene = fixture.Flat;
        var pursuit = new GroundPursuit(scene.Navigation, new PathFollowConfig { ReplanCooldownSeconds = 10 });
        var body = scene.Body();
        var ready = pursuit.Admit(body, Tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, ready.Status);
        if (consumed) body = scene.Body(1.7f);
        var moved = scene.Target(2.25f);
        var update = pursuit.Tick(body, Tuning, moved, 0.01f, scene.Context);
        Assert.Equal(GroundPursuitStatus.Travelling, update.Status);
        AssertCommand(update, positive: consumed);
        if (!consumed)
        {
            Assert.Equal(ready.Direction, update.Direction);
            Assert.Equal(0, update.RemainingPathLength);
            var refreshed = pursuit.Tick(body, Tuning, moved, 10, scene.Context);
            AssertCommand(refreshed, positive: true);
            Assert.True(refreshed.RemainingPathLength > ready.RemainingPathLength);
        }
    }

    [Fact]
    public void FreshTerminalRouteKeepsCooldownAndCannotPretendToArrive()
    {
        var scene = fixture.Flat;
        var pursuit = new GroundPursuit(scene.Navigation, new PathFollowConfig { ReplanCooldownSeconds = 10 });
        var body = scene.Body(1.25f);
        var ready = pursuit.Admit(body, Tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, ready.Status);
        var held = pursuit.Tick(body, Tuning, scene.Target(2.25f), 0.01f, scene.Context);
        Assert.Equal(GroundPursuitStatus.Travelling, held.Status);
        AssertCommand(held, positive: false);
        Assert.Equal(0, held.RemainingPathLength);
        AssertCommand(pursuit.Tick(body, Tuning, scene.Target(2.25f), 10, scene.Context), positive: true);
    }

    [Fact]
    public void ReplansApplyPathCapButNeverReapplyTriggerLimit()
    {
        var scene = fixture.Flat;
        var pursuit = new GroundPursuit(scene.Navigation);
        var body = scene.Body();
        Assert.Equal(GroundPursuitStatus.Ready, pursuit.Admit(body, Tuning, scene.Target(), new(3, 4, 0.1f), scene.Context).Status);
        var beyondTrigger = pursuit.Tick(body, Tuning, scene.Target(2.25f), 1, scene.Context);
        Assert.Equal(GroundPursuitStatus.Travelling, beyondTrigger.Status);
        AssertCommand(beyondTrigger, positive: true);
        var beyondCap = pursuit.Tick(body, Tuning, scene.Target(3.25f), 1, scene.Context);
        Assert.Equal(GroundPursuitStatus.TooLong, beyondCap.Status);
    }

    [Fact]
    public void CorridorExitHoldsThenFindsANewGuardedEntry()
    {
        var scene = fixture.Flat;
        var pursuit = new GroundPursuit(scene.Navigation, new PathFollowConfig { CorridorTolerance = 0.2f, ReplanCooldownSeconds = 1 });
        var ready = pursuit.Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, ready.Status);
        var offRoute = scene.Body(z: 1.25f);
        var held = pursuit.Tick(offRoute, Tuning, scene.Target(), 0.1f, scene.Context);
        Assert.Equal(GroundPursuitStatus.Travelling, held.Status);
        Assert.Equal(0, held.RemainingPathLength);
        AssertCommand(held, positive: false);
        var refreshed = pursuit.Tick(offRoute, Tuning, scene.Target(), 1, scene.Context);
        AssertCommand(refreshed, positive: true);
        Assert.True(refreshed.Direction.Y < 0);
    }

    [Fact]
    public void LossAndResetDiscardTheRouteBeforeTargetReplacement()
    {
        var scene = fixture.Flat;
        var pursuit = new GroundPursuit(scene.Navigation, new PathFollowConfig { ReplanCooldownSeconds = 10 });
        Assert.Equal(GroundPursuitStatus.Ready, pursuit.Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context).Status);
        Assert.Equal(GroundPursuitStatus.TargetLost, pursuit.Tick(scene.Body(), Tuning, null, 0.01f, scene.Context).Status);
        Assert.Equal(GroundPursuitDetail.NotAdmitted, pursuit.Tick(scene.Body(), Tuning, scene.Target(), 0.01f, scene.Context).Detail);
        Assert.Equal(GroundPursuitStatus.Ready, pursuit.Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context).Status);
        pursuit.Reset();
        Assert.Equal(GroundPursuitDetail.NotAdmitted, pursuit.Tick(scene.Body(), Tuning, scene.Target(), 0.01f, scene.Context).Detail);
        var replaced = pursuit.Admit(scene.Body(1.75f), Tuning, scene.Target(-1.25f), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, replaced.Status);
        Assert.True(replaced.Direction.X < 0);
    }

    [Theory]
    [InlineData(0, GroundPursuitDetail.Airborne)]
    [InlineData(1, GroundPursuitDetail.Swimming)]
    [InlineData(2, GroundPursuitDetail.JumpPending)]
    [InlineData(3, GroundPursuitDetail.ActiveCommitment)]
    [InlineData(4, GroundPursuitDetail.ProfileMismatch)]
    public void UnsupportedStateAndProfileRefuseWithTypedDetail(int fault, GroundPursuitDetail detail)
    {
        var scene = fixture.Flat;
        var body = scene.Body();
        var tuning = Tuning;
        var pursuit = new GroundPursuit(scene.Navigation);
        Assert.Equal(GroundPursuitStatus.Ready, pursuit.Admit(body, tuning, scene.Target(), Limits, scene.Context).Status);
        switch (fault)
        {
            case 0: body.Grounded = false; break;
            case 1: body.Swimming = true; break;
            case 2: body.JumpBufferRemaining = 0.1f; break;
            case 3: body.Commitment = new() { Phase = MovementCommitmentPhase.Airborne }; break;
            case 4: tuning = tuning with { CapsuleRadius = tuning.CapsuleRadius + 0.01f }; break;
        }
        var duringTravel = pursuit.Tick(body, tuning, scene.Target(), 0.1f, scene.Context);
        Assert.Equal(GroundPursuitStatus.UnsupportedState, duringTravel.Status);
        Assert.Equal(detail, duringTravel.Detail);
        var refused = pursuit.Admit(body, tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.UnsupportedState, refused.Status);
        Assert.Equal(detail, refused.Detail);
    }

    [Theory]
    [InlineData(0, 5, 0)]
    [InlineData(float.NaN, 5, 0)]
    [InlineData(float.PositiveInfinity, 5, 0)]
    [InlineData(5, 0, 0)]
    [InlineData(5, float.NaN, 0)]
    [InlineData(5, float.PositiveInfinity, 0)]
    [InlineData(5, 5, -1)]
    [InlineData(5, 5, float.NaN)]
    [InlineData(5, 5, float.PositiveInfinity)]
    public void MalformedAbilityLimitsRemainParameterErrors(float trigger, float cap, float stop)
    {
        var scene = fixture.Flat;
        var pursuit = new GroundPursuit(scene.Navigation);
        Assert.Equal(GroundPursuitStatus.Ready, pursuit.Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context).Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => pursuit.Admit(scene.Body(), Tuning, scene.Target(),
            new(trigger, cap, stop), scene.Context));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void InvalidTickTimeRemainsAParameterError(float dt)
    {
        var scene = fixture.Flat;
        var pursuit = new GroundPursuit(scene.Navigation);
        Assert.Equal(GroundPursuitStatus.Ready, pursuit.Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context).Status);
        Assert.Throws<ArgumentOutOfRangeException>(() => pursuit.Tick(scene.Body(), Tuning, scene.Target(), dt, scene.Context));
    }

    [Fact]
    public void CapsuleFeetAnchorAndConservativeFiniteExtentGuardAreRetained()
    {
        var scene = fixture.Flat;
        var pursuit = new GroundPursuit(scene.Navigation);
        var capsule = ReachTarget.Capsule(new(new(1.75f, 2, 0.25f), 0.3f, 2));
        Assert.Equal(GroundPursuitStatus.Ready, pursuit.Admit(scene.Body(), Tuning, capsule, Limits, scene.Context).Status);
        // Keep its vertical extent away so physical reach cannot short circuit the invalid planning bound.
        var enormous = ReachTarget.Box(new(1.75f, 10, 0.25f), new(float.MaxValue, 1, float.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => pursuit.Admit(scene.Body(), Tuning, enormous, new(20, 20, 0), scene.Context));
    }

    private static GroundPursuit Fixed(PursuitScene scene, NavPath path, Func<Vector3, Vector3, bool>? guard = null)
        => new(new MoveToRangeTests.ScriptPlanner((_, _) => path), scene.Navigation.Space,
            guard ?? scene.Navigation.AllowsSegment);

    private static void AssertCommand(GroundPursuitResult result, bool positive)
    {
        Assert.True(float.IsFinite(result.Direction.X) && float.IsFinite(result.Direction.Y));
        Assert.InRange(result.Direction.Length(), 0.99999f, 1.00001f);
        Assert.True(float.IsFinite(result.RemainingPathLength));
        if (positive) Assert.True(result.RemainingPathLength > 0);
        else Assert.True(result.RemainingPathLength >= 0);
        // Exercise the earlier task's public value guard as the game does, without a server loopback.
        if (result.Status == GroundPursuitStatus.Ready)
            _ = MovementCommitment.ForGroundTravel(1, result.Direction, 5, result.RemainingPathLength);
    }
}
