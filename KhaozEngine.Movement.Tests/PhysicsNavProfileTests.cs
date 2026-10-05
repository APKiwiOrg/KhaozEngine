using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class PhysicsNavProfileTests
{
    private static MoveTuning Tuning => GroundTraversalProbeTests.Tuning;
    private static readonly PhysicsNavBakeOptions Options = new(-1.5f, -0.5f, 1.5f, 0.5f,
        1f, 5f, 6f, 0.8f, 128, 512);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThinWallBetweenColumnsRefusesHorizontalEdges(bool alongZ)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        Vector3 extents = alongZ ? new(2f, 1f, 0.05f) : new(0.05f, 1f, 2f);
        Vector3 wall = alongZ ? new(0f, 1f, 0.5f) : new(0.5f, 1f, 0f);
        world.AddStatic(new BoxShape(extents), Pose.At(wall));
        using var bake = Capture(world, alongZ ? Options with
        {
            MinX = -0.5f,
            MaxX = 0.5f,
            MinZ = -1.5f,
            MaxZ = 1.5f,
        } : Options);
        GroundNavigation nav = bake.BuildProfile(Tuning, default);
        (int x, int z) = alongZ ? (0, 1) : (1, 0);
        (int nx, int nz) = alongZ ? (0, 2) : (2, 0);
        Vector3 endpoint = alongZ ? Vector3.UnitZ : Vector3.UnitX;

        Assert.True(nav.Graph.IsNodePassable(0, x, z));
        Assert.True(nav.Graph.IsNodePassable(0, nx, nz));
        Assert.False(nav.Graph.CanTraverse(0, x, z, 0, nx, nz));
        Assert.False(nav.Graph.CanTraverse(0, nx, nz, 0, x, z));
        Assert.False(nav.AllowsSegment(Vector3.Zero, endpoint));
        Assert.False(nav.AllowsSegment(endpoint, Vector3.Zero));
        Assert.NotEqual(NavPathStatus.Complete, Route(nav, Vector3.Zero, endpoint).Status);
    }

    [Fact]
    public void ReportedFloorWithSolidInItsFootprintCannotAcceptABody()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(0.05f, 1f, 0.25f)), Pose.At(new Vector3(0.15f, 1f, 0f)));
        using var bake = Capture(world);
        Assert.Equal(0f, bake.Columns.GetColumn(1, 0)[0].Height, 5);
        Assert.True(float.IsPositiveInfinity(bake.Columns.GetColumn(1, 0)[0].Headroom));

        GroundNavigation nav = bake.BuildProfile(Tuning, default);

        Assert.False(nav.Graph.IsNodePassable(0, 1, 0));
        Assert.False(nav.AllowsSegment(Vector3.Zero, Vector3.Zero));
    }

    [Fact]
    public void OneMetreDoorAcceptsSmallPlayerAndRefusesWideBody()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(0.05f, 1f, 1f)), Pose.At(new Vector3(0f, 1f, 1.5f)));
        world.AddStatic(new BoxShape(new Vector3(0.05f, 1f, 1f)), Pose.At(new Vector3(0f, 1f, -1.5f)));
        using var bake = Capture(world, Options with
        {
            MinX = -2.25f,
            MaxX = 2.25f,
            MinZ = -1.25f,
            MaxZ = 1.25f,
            CellSize = 0.5f,
        });
        GroundNavigation player = bake.BuildProfile(Tuning, default);
        GroundNavigation wide = bake.BuildProfile(Tuning with { CapsuleRadius = 0.55f }, default);

        NavPath route = Route(player, -Vector3.UnitX, Vector3.UnitX);
        Assert.Equal(NavPathStatus.Complete, route.Status);
        Assert.NotEqual(NavPathStatus.Complete, Route(wide, -Vector3.UnitX, Vector3.UnitX).Status);
        Assert.True(route.Waypoints.Count > 1);
        Assert.DoesNotContain(route.Waypoints, waypoint => waypoint.Kind == NavWaypointKind.Hop);
        Assert.DoesNotContain(player.Space.Links, link => link.Kind == NavLinkKind.Hop);
        var context = new GroundMoveContext((_, _) => 0f, physics: world);
        var body = new MoveState { Position = new Vector3(-1f, 0.75f, 0f), Grounded = true, SpeedScale = 1f };
        foreach (NavWaypoint waypoint in route.Waypoints)
        {
            for (int slice = 0; slice < 64; slice++)
            {
                Vector2 delta = waypoint.Position - new Vector2(body.Position.X, body.Position.Z);
                if (delta.Length() <= 0.001f) break;
                Vector2 command = delta / delta.Length() * MathF.Min(1f, delta.Length() / (Tuning.WalkSpeed / 30f));
                MoveState next = context.Step(body, command, false, 1f / 30f, Tuning);
                body = next;
            }
            Assert.InRange(Vector2.Distance(waypoint.Position, new Vector2(body.Position.X, body.Position.Z)), 0f, 0.001f);
            Assert.True(body.Grounded);
            Assert.Equal(0.75f, body.Position.Y, 5);
        }
        Assert.InRange(Vector3.Distance(new Vector3(1f, 0.75f, 0f), body.Position), 0f, 0.001f);
    }

    [Theory]
    [InlineData(0x01u)]
    [InlineData(0x07u)]
    public void WholeCircleRequiresAllBitsAndExcludesEveryForbiddenBit(uint outside)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var bake = Capture(world, Options with { MinZ = -1.5f, MaxZ = 1.5f },
            feet => feet.X >= 1f ? outside : 0x03u);
        var filter = new NavAreaFilter(0x03u, 0x04u);
        GroundNavigation small = bake.BuildProfile(Tuning, filter);
        GroundNavigation wide = bake.BuildProfile(Tuning with { CapsuleRadius = 0.6f }, filter);

        Assert.True(small.Graph.IsNodePassable(0, 1, 1));
        Assert.False(wide.Graph.IsNodePassable(0, 1, 1));
        Assert.False(wide.AllowsSegment(Vector3.Zero, Vector3.Zero));
        Assert.False(small.AllowsSegment(Vector3.Zero, Vector3.UnitX));
    }

    [Fact]
    public void FootprintAreaGuardRejectsAnIntermediateBoundaryCrossing()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var bake = Capture(world, Options with { MinZ = -1.5f, MaxZ = 1.5f },
            feet => feet.X < 1f ? 0x01u : 0u);
        GroundNavigation pen = bake.BuildProfile(Tuning, new NavAreaFilter(0x01u, 0u));

        Assert.True(pen.AllowsSegment(Vector3.Zero, new Vector3(0.25f, 0f, 0f)));
        Assert.False(pen.AllowsSegment(Vector3.Zero, new Vector3(0.4f, 0f, 0f)));
    }

    [Fact]
    public void CircleCornerUsesDistanceRatherThanItsBoundingSquare()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var bake = Capture(world, Options with { MinZ = -1.5f, MaxZ = 1.5f },
            feet => feet.X > 0.5f && feet.Z > 0.5f ? 0x02u : 0x01u);
        var filter = new NavAreaFilter(0x01u, 0x02u);

        GroundNavigation inside = bake.BuildProfile(Tuning with { CapsuleRadius = 0.7f }, filter);
        GroundNavigation touches = bake.BuildProfile(Tuning with { CapsuleRadius = 0.71f }, filter);

        Assert.True(inside.Graph.IsNodePassable(0, 1, 1));
        Assert.False(touches.Graph.IsNodePassable(0, 1, 1));
    }

    [Theory]
    [InlineData(0.2f, 0.2f, -0.5f, 0.5f)]
    [InlineData(0.2f, 0.2f, 0.5f, -0.5f)]
    [InlineData(-0.2f, -0.2f, 0.5f, -0.5f)]
    [InlineData(-0.2f, -0.2f, -0.5f, 0.5f)]
    [InlineData(1.2f, 1.2f, -0.5f, -0.5f)]
    [InlineData(-1.2f, -1.2f, 0.5f, 0.5f)]
    [InlineData(1.2f, -1.2f, -0.5f, 0.5f)]
    [InlineData(-1.2f, 1.2f, 0.5f, -0.5f)]
    public void HalfOpenCornerEndpointDoesNotWalkPastItsOwningCell(float fromX, float fromZ, float toX, float toZ)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var bake = Capture(world, Options with { MinX = -2.5f, MaxX = 2.5f, MinZ = -2.5f, MaxZ = 2.5f });
        GroundNavigation nav = bake.BuildProfile(Tuning, default);
        Vector3 from = new(fromX, 0f, fromZ), to = new(toX, 0f, toZ);

        Assert.True(nav.AllowsSegment(from, to));
        Assert.True(nav.AllowsSegment(to, from));
    }

    [Fact]
    public void SurfaceHeightSelectsDeckTagsInsteadOfWaterBelow()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(4f, 0.1f, 4f)), Pose.At(new Vector3(0f, 2.9f, 0f)));
        using var bake = Capture(world, classify: feet => feet.Y < 1f ? 0x02u : 0x01u);
        GroundNavigation dry = bake.BuildProfile(Tuning, new NavAreaFilter(0x01u, 0x02u));
        GroundNavigation wet = bake.BuildProfile(Tuning, new NavAreaFilter(0x02u, 0x01u));

        Assert.Equal(NavPathStatus.Complete, Route(dry, new Vector3(-1f, 3f, 0f), new Vector3(1f, 3f, 0f)).Status);
        Assert.False(dry.AllowsSegment(Vector3.Zero, Vector3.UnitX));
        Assert.True(wet.AllowsSegment(Vector3.Zero, Vector3.UnitX));
        Assert.False(wet.AllowsSegment(new Vector3(0f, 3f, 0f), new Vector3(1f, 3f, 0f)));
    }

    [Fact]
    public void NarrowAcceptedCorridorIsNotErodedTwice()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(3f, 1f, 0.2f)), Pose.At(new Vector3(0f, 1f, 0.65f)));
        world.AddStatic(new BoxShape(new Vector3(3f, 1f, 0.2f)), Pose.At(new Vector3(0f, 1f, -0.65f)));
        using var bake = Capture(world, Options with
        {
            MinX = -1.125f,
            MaxX = 1.125f,
            MinZ = -0.375f,
            MaxZ = 0.375f,
            CellSize = 0.25f,
        });
        GroundNavigation nav = bake.BuildProfile(Tuning with { CapsuleRadius = 0.3f }, default);

        Assert.False(nav.Space.Layers[0].IsPassable(4, 1, 0.3f));
        Assert.True(nav.Graph.IsNodePassable(0, 4, 1));
        Assert.Equal(NavPathStatus.Complete, Route(nav, new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f)).Status);
    }

    [Fact]
    public void StepProbeDoesNotAssumeReverseDirectionSucceeds()
    {
        using var world = GroundTraversalProbeTests.StepWorld();
        using var bake = Capture(world, Options with { MinX = -1f, MaxX = 1f });
        GroundNavigation nav = bake.BuildProfile(Tuning with { MaxStepClimbSpeed = 0.001f }, default);

        Assert.True(nav.Graph.IsNodePassable(0, 0, 0));
        Assert.True(nav.Graph.IsNodePassable(0, 1, 0));
        Assert.False(nav.Graph.CanTraverse(0, 0, 0, 0, 1, 0));
        Assert.True(nav.Graph.CanTraverse(0, 1, 0, 0, 0, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CandidateStairLinksRequireTheSamePhysicalProof(bool fence)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, 0.05f, 2f)), Pose.At(new Vector3(-2f, 0.05f, 0f)));
        world.AddStatic(new BoxShape(new Vector3(2f, 0.025f, 2f)), Pose.At(new Vector3(2f, 0.275f, 0f)));
        if (fence)
            world.AddStatic(new BoxShape(new Vector3(0.05f, 1f, 2f)), Pose.At(new Vector3(0f, 1f, 0f)));
        using var bake = Capture(world, Options with { MinX = -1f, MaxX = 1f });
        MoveTuning tiny = Tuning with { CapsuleRadius = 0.04f, CapsuleHalfHeight = 0.1f, MaxStepClimbSpeed = 0f };
        GroundNavigation nav = bake.BuildProfile(tiny, default);

        Assert.NotEmpty(nav.Space.Links);
        NavLink seam = Assert.Single(nav.Space.Links, link => link.FromX == 0 && link.ToX == 1);
        Assert.Equal(NavLinkKind.Stair, seam.Kind);
        Assert.Equal(!fence, nav.Graph.CanTraverse(seam.FromLayer, seam.FromX, seam.FromZ,
            seam.ToLayer, seam.ToX, seam.ToZ));
        Assert.Equal(!fence, nav.AllowsSegment(new Vector3(-0.5f, 0.1f, 0f), new Vector3(0.5f, 0.3f, 0f)));
        if (!fence)
        {
            MoveTuning proof = tiny with { WalkSpeed = 1f, RunSpeed = 1f };
            var context = new GroundMoveContext((_, _) => 0f, physics: world);
            var body = new MoveState { Position = new Vector3(-0.5f, 0.2f, 0f), Grounded = true };
            for (int slice = 0; slice < 64; slice++)
            {
                float fraction = Math.Clamp((0.5f - body.Position.X) * 30f, -1f, 1f);
                body = context.Step(body, new Vector2(fraction, 0f), false, 1f / 30f, proof);
            }
            Assert.True(body.Grounded);
            Assert.InRange(Vector3.Distance(new Vector3(0.5f, 0.4f, 0f), body.Position), 0f, 0.001f);
        }
    }

    [Fact]
    public void HighRiseAndMissingColumnNeverGenerateHopRoutes()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(2f, 0.5f, 2f)), Pose.At(new Vector3(2f, 0.5f, 0f)));
        using var bake = Capture(world, Options with { MinX = -1f, MaxX = 1f });
        GroundNavigation nav = bake.BuildProfile(Tuning, default);

        Assert.DoesNotContain(nav.Space.Links, link => link.Kind == NavLinkKind.Hop);
        Assert.DoesNotContain(nav.Graph.Links, link => link.Kind == NavLinkKind.Hop);
        Assert.NotEqual(NavPathStatus.Complete, Route(nav, new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 1f, 0f)).Status);
    }

    [Fact]
    public void QueriesOwnSnapshotsAfterClassifierBuilderAndWorldAreGone()
    {
        GroundNavigation nav;
        using (var world = GroundTraversalProbeTests.FlatWorld())
        {
            uint tags = 0x01u;
            bool closed = false;
            using var bake = Capture(world, classify: _ => closed ? throw new InvalidOperationException() : tags);
            tags = 0u;
            closed = true;
            nav = bake.BuildProfile(Tuning, new NavAreaFilter(0x01u, 0u));
            bake.Dispose();
            Assert.Throws<ObjectDisposedException>(() => bake.BuildProfile(Tuning, default));
        }

        Assert.True(nav.AllowsSegment(-Vector3.UnitX, Vector3.UnitX));
        Assert.Equal(NavPathStatus.Complete, Route(nav, -Vector3.UnitX, Vector3.UnitX).Status);
        var goal = new NavGoalRegion(Vector3.UnitX, 0.1f, feet => Vector3.Distance(feet, Vector3.UnitX) < 0.01f);
        Assert.Equal(NavPathStatus.Complete, nav.Planner.FindPath(-Vector3.UnitX, goal, nav.AgentRadius, PathQueryBudget.Default).Status);
        Assert.Same(nav.Graph.Space, nav.Space);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProfilesAndCoreProofsUseAbsoluteFeetAcrossARebasedWorld(bool rebase)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        if (rebase) world.Rebase(new Vector3(100f, 20f, -80f));
        using var bake = Capture(world);
        GroundNavigation nav = bake.BuildProfile(Tuning, default);

        Assert.True(nav.AllowsSegment(-Vector3.UnitX, Vector3.UnitX));
        Assert.Equal(NavPathStatus.Complete, Route(nav, -Vector3.UnitX, Vector3.UnitX).Status);
    }

    [Fact]
    public void ProfilesRequireTheCapturedOriginAndSlopeClass()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var bake = Capture(world);

        Assert.Throws<ArgumentException>(() => bake.BuildProfile(Tuning with { MaxSlopeRadians = 0.7f }, default));
        world.Rebase(new Vector3(100f, 20f, -80f));
        Assert.Throws<InvalidOperationException>(() => bake.BuildProfile(Tuning, default));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RuntimeTuningRejectsProfileGeometryMismatch(int field)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var bake = Capture(world);
        GroundNavigation nav = bake.BuildProfile(Tuning, default);
        MoveTuning changed = field switch
        {
            0 => Tuning with { CapsuleRadius = 0.21f },
            1 => Tuning with { CapsuleHalfHeight = 0.8f },
            2 => Tuning with { MaxSlopeRadians = 0.7f },
            _ => Tuning with { StepHeight = 0.3f },
        };

        Assert.Throws<ArgumentException>(() => nav.ValidateTuning(changed));
        nav.ValidateTuning(Tuning with { WalkSpeed = 0f, RunSpeed = 100f, MaxStepClimbSpeed = 0f });
        Assert.Throws<ArgumentException>(() => nav.Planner.FindPath(Vector3.Zero, Vector3.UnitX, 0.21f, PathQueryBudget.Default));
    }

    [Fact]
    public void UnknownPaddedOffGridAndWrongHeightEndpointsAreRefused()
    {
        using var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new Vector3(0.4f, 0.1f, 1f)), Pose.At(new Vector3(-1f, -0.1f, 0f)));
        using var bake = Capture(world, Options with { MaxX = 0.7f });
        GroundNavigation nav = bake.BuildProfile(Tuning, default);
        Vector3 start = -Vector3.UnitX;
        Vector3[] invalid = [Vector3.Zero, Vector3.UnitX, new(-1.6f, 0f, 0f), new(-1f, 2f, 0f),
            new(float.NaN, 0f, 0f), new(float.PositiveInfinity, 0f, 0f)];

        foreach (Vector3 endpoint in invalid)
        {
            Assert.False(nav.AllowsSegment(start, endpoint));
            Assert.NotEqual(NavPathStatus.Complete, Route(nav, start, endpoint).Status);
        }
        Assert.True(nav.AllowsSegment(start, start));
    }

    [Fact]
    public void LayerCellBudgetIsEnforcedBeforeDenseProfileAllocation()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new Vector3(4f, 0.1f, 4f)), Pose.At(new Vector3(0f, 2.9f, 0f)));
        using var bake = Capture(world, Options with { MaxLayerCells = 3 });

        Assert.Throws<ArgumentOutOfRangeException>(() => bake.BuildProfile(Tuning, default));
    }

    private static PhysicsNavBake Capture(BepuPhysicsWorld world, PhysicsNavBakeOptions? options = null,
        NavAreaClassifier? classify = null)
        => PhysicsNavBake.Capture(new GroundMoveContext((_, _) => 0f, physics: world),
            options ?? Options, classify ?? (_ => 0u));

    private static NavPath Route(GroundNavigation nav, Vector3 start, Vector3 goal)
        => nav.Planner.FindPath(start, goal, nav.AgentRadius, PathQueryBudget.Default);
}
