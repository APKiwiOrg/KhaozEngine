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
