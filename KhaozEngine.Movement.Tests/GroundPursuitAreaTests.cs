using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;
using static KhaozEngine.Tests.Movement.Fixtures.GroundPursuitFixture;

namespace KhaozEngine.Tests.Movement;

// Regression #1706. Wading profiles must support a dry-only pursuit without a second bake.
public class GroundPursuitAreaTests
{
    private const uint Wet = 1, Forbidden = 2;
    private static GroundPursuitSettings Limits => new(10, 20, 0.1f);
    private static Vector3 Feet(MoveState body) => body.Position - Vector3.UnitY * Tuning.CapsuleHalfHeight;

    [Fact]
    public void ExclusionFindsTheDryBridgeAndEverySteeredSegmentStaysDryUntilArrival()
    {
        using var scene = new PursuitScene(false, false, p => p.X is >= 0 and < 1 && p.Z < 2 ? Wet : 0);
        var dry = scene.Navigation.ExcludingAreas(Wet);
        var pursuit = new GroundPursuit(dry, new PathFollowConfig { AcceptRadius = 0.05f });
        var body = scene.Body();
        var target = scene.Target();
        var ready = pursuit.Admit(body, Tuning, target, Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, ready.Status);
        Assert.True(ready.RemainingPathLength > 3);
        Assert.True(ready.Direction.Y > 0);
        body.Commitment = MovementCommitment.ForGroundTravel(1, ready.Direction, 2, ready.RemainingPathLength, timeoutSeconds: 20);
        bool crossedBridge = false, arrived = false;
        for (int tick = 0; tick < 400; tick++)
        {
            var update = pursuit.Tick(body, Tuning, target, 0.05f, scene.Context);
            if (update.Status == GroundPursuitStatus.Arrived) { arrived = true; break; }
            Assert.Equal(GroundPursuitStatus.Travelling, update.Status);
            body.Commitment = GroundTravelCommitment.Steer(body.Commitment, 1, update.Direction, update.RemainingPathLength);
            var next = scene.Context.Step(body, Vector2.Zero, false, 0.05f, Tuning);
            Assert.True(dry.AllowsSegment(Feet(body), Feet(next)));
            crossedBridge |= next.Position.X is >= 0 and < 1 && next.Position.Z >= 2;
            body = next;
            Assert.True(body.Commitment.IsActive);
        }
        Assert.True(crossedBridge);
        Assert.True(arrived);
        Assert.Equal(GroundPursuitStatus.Ready, new GroundPursuit(scene.Navigation)
            .Admit(scene.Body(), Tuning, target, Limits, scene.Context).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OnlyWetRouteRefusesAtAdmissionOrTheNextTargetReplan(bool replan)
    {
        using var scene = new PursuitScene(false, false, p => p.X is >= 0 and < 1 ? Wet : 0);
        var pursuit = new GroundPursuit(scene.Navigation.ExcludingAreas(Wet));
        var body = scene.Body();
        if (replan)
        {
            var ready = pursuit.Admit(body, Tuning, scene.Target(-0.75f, 1.25f), Limits, scene.Context);
            Assert.Equal(GroundPursuitStatus.Ready, ready.Status);
            body.Commitment = MovementCommitment.ForGroundTravel(1, ready.Direction, 2, ready.RemainingPathLength);
        }
        var result = replan ? pursuit.Tick(body, Tuning, scene.Target(), 1, scene.Context)
            : pursuit.Admit(body, Tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.NoPath, result.Status);
        Assert.Equal(GroundPursuitDetail.ExcludedArea, result.Detail);
        Assert.Equal(Vector2.Zero, result.Direction);
        Assert.Equal(0, result.RemainingPathLength);
        Assert.Equal(GroundPursuitStatus.Ready, new GroundPursuit(scene.Navigation)
            .Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context).Status);
    }

    [Theory]
    [InlineData(0u, Forbidden)]
    [InlineData(Forbidden, 0u)]
    public void ExclusionCannotWidenBakedRequiredOrExcludedBits(uint required, uint excluded)
    {
        using var scene = new PursuitScene(false, false,
            p => p.X is >= 0 and < 1 ? (required == 0 ? Forbidden : 0) : required,
            new(required, excluded));
        var narrowed = scene.Navigation.ExcludingAreas(Wet);
        var result = new GroundPursuit(narrowed).Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.NoPath, result.Status);
        Assert.NotEqual(GroundPursuitDetail.ExcludedArea, result.Detail);
        Assert.False(narrowed.AllowsSegment(Feet(scene.Body()), Feet(scene.Body(1.75f))));
    }

    [Fact]
    public void ExcludingARequiredBitRefusesInsteadOfReplacingTheBakedFilter()
    {
        using var scene = new PursuitScene(false, false, _ => Wet, new(Wet, 0));
        var result = new GroundPursuit(scene.Navigation.ExcludingAreas(Wet))
            .Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.NoPath, result.Status);
        Assert.Equal(GroundPursuitDetail.ExcludedArea, result.Detail);
        Assert.Equal(Vector2.Zero, result.Direction);
    }

    [Fact]
    public void ActualFeetCannotEnterThroughAnExcludedFootprintEvenWhenTheirOwnCellIsDry()
    {
        using var scene = new PursuitScene(false, false, p => p.X is >= 0 and < 0.5f ? Wet : 0);
        var body = scene.Body(-0.1f);
        var target = scene.Target(-1.25f, 1.25f);
        var dry = scene.Navigation.ExcludingAreas(Wet);
        Assert.Equal(NavPathStatus.Complete, scene.Navigation.Planner.FindPath(Feet(body), Feet(scene.Body(-1.25f, 1.25f)),
            Tuning.CapsuleRadius, PathQueryBudget.Default).Status);
        Assert.False(dry.AllowsSegment(Feet(body), Feet(scene.Body(-1.25f, 1.25f))));
        var result = new GroundPursuit(dry).Admit(body, Tuning, target, Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.NoPath, result.Status);
        Assert.Equal(GroundPursuitDetail.ExcludedArea, result.Detail);
        Assert.Equal(Vector2.Zero, result.Direction);
    }

    [Fact]
    public void ChainedExclusionsAccumulateWhileZeroKeepsTheOriginalProfile()
    {
        using var scene = new PursuitScene(false, false, p => p.X is >= 0 and < 1 ? Forbidden : 0);
        Assert.Same(scene.Navigation, scene.Navigation.ExcludingAreas(0));
        var first = scene.Navigation.ExcludingAreas(Forbidden);
        var combined = first.ExcludingAreas(Wet);
        Assert.Same(scene.Navigation.Space, combined.Space);
        Assert.Same(scene.Navigation.Graph, combined.Graph);
        Assert.Same(scene.Navigation.Footprint.Columns, combined.Footprint.Columns);
        var result = new GroundPursuit(combined).Admit(scene.Body(), Tuning, scene.Target(), Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.NoPath, result.Status);
        Assert.Equal(GroundPursuitDetail.ExcludedArea, result.Detail);
    }

    [Fact]
    public void DryLinkEndpointsCannotShortcutAcrossAWetInteriorInsteadOfTakingTheBridge()
    {
        using var scene = new PursuitScene(false, false, p => p.X is >= 0 and < 1 && p.Z < 2 ? Wet : 0);
        var grid = scene.Navigation.Space.Layers[0];
        var body = scene.Body();
        var target = scene.Target();
        var (x, z) = grid.CellOf(body.Position.X, body.Position.Z);
        var (tx, tz) = grid.CellOf(1.75f, 0.25f);
        // A candidate Stair link is an edge, not permission to skip the area footprint between its endpoints.
        var link = new NavLink(0, x, z, 0, tx, tz);
        var space = new NavSpace(scene.Navigation.Space.Layers, [link]);
        var graph = new NavTraversalGraph(space, Tuning.CapsuleRadius, Tuning.CapsuleHalfHeight * 2,
            scene.Navigation.Graph.Layers, [link]);
        var navigation = new GroundNavigation(graph, Tuning, scene.Navigation.Footprint);
        var result = new GroundPursuit(navigation.ExcludingAreas(Wet))
            .Admit(body, Tuning, target, Limits, scene.Context);
        Assert.Equal(GroundPursuitStatus.Ready, result.Status);
        Assert.True(result.RemainingPathLength > 3);
        Assert.True(result.Direction.Y > 0);
    }

    [Fact]
    public void LoadedBakeKeepsSurfaceAreasForPursuitExclusionWithoutPhysicsOrAnotherProfile()
    {
        var baked = GroundNavigationBakeRoundTripTests.Bake("pool");
        var navigation = GroundNavigationBakeRoundTripTests.LoadOk(baked.File, baked.Expected).GetProfile("player");
        var tuning = GroundTraversalProbeTests.Tuning;
        var grid = navigation.Space.Layers[0];
        Vector2 start = grid.CellCenter(0, 0), end = grid.CellCenter(3, 0);
        var body = new MoveState
        {
            Position = new(start.X, grid.RestingHeightAt(0, 0)!.Value + tuning.CapsuleHalfHeight, start.Y),
            Grounded = true,
        };
        var target = ReachTarget.Point(new(end.X, body.Position.Y, end.Y));
        using var context = new GroundMoveContext((_, _) => 0);
        var result = new GroundPursuit(navigation.ExcludingAreas(1))
            .Admit(body, tuning, target, Limits, context);
        Assert.Equal(GroundPursuitStatus.NoPath, result.Status);
        Assert.Equal(GroundPursuitDetail.ExcludedArea, result.Detail);
        Assert.Equal(GroundPursuitStatus.Ready,
            new GroundPursuit(navigation).Admit(body, tuning, target, Limits, context).Status);
    }

    [Fact]
    public void AnUnmatchedExclusionPreservesTheProfilesDryDropTopology()
    {
        using var scene = new PursuitScene(true, false, cliffHeight: 1.8f);
        var start = Feet(scene.Body(surface: 1.8f));
        var end = Feet(scene.Body(1.75f));
        var ordinary = scene.Navigation.Planner.FindPath(start, end, Tuning.CapsuleRadius, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, ordinary.Status);
        Assert.Contains(ordinary.Waypoints, waypoint => waypoint.Kind == NavWaypointKind.Drop);
        var narrowed = scene.Navigation.ExcludingAreas(Wet).Planner.FindPath(start, end,
            Tuning.CapsuleRadius, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, narrowed.Status);
        Assert.Contains(narrowed.Waypoints, waypoint => waypoint.Kind == NavWaypointKind.Drop);
    }
}
