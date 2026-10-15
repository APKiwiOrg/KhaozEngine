using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;
using static KhaozEngine.Tests.Movement.StableLegacyReadScopeTests;

namespace KhaozEngine.Tests.Movement;

public sealed class StableLegacyReadParityTests
{
    [Theory]
    [InlineData(0f, false)]
    [InlineData(0.125f, false)]
    [InlineData(0f, true)]
    [InlineData(0.125f, true)]
    public void FlatAndSlopedCapsulesRetainCanonicalBytesAcrossRebase(float slope, bool rebased)
    {
        using var world = new BepuPhysicsWorld();
        Quaternion rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan(slope));
        StaticHandle floor = world.AddStatic(new BoxShape(new(8, 0.1f, 8)), new Pose(new(0, -0.1f, 0), rotation));
        if (rebased) world.Rebase(new(128, 16, -64));
        // Capture the complete slope and move on its matching analytic ground, as a terrain consumer does.
        using var view = world.CreateQueryViewExcludingStatics([floor]);
        float intercept = 0.1f / MathF.Cos(MathF.Atan(slope)) - 0.1f;
        using var context = new GroundMoveContext((x, _) => slope * x + intercept,
            (_, _) => Vector3.Normalize(new(-slope, 1, 0)), world, null, null, view);
        using var capture = PhysicsNavBake.Capture(context, Options with { MinZ = -1.5f, MaxZ = 1.5f }, _ => 7);
        GroundNavigationBake bake = SameBytes(capture,
            new("narrow", Tuning, default), new("wide", Tuning with { CapsuleRadius = 0.6f, CapsuleHalfHeight = 0.9f }, default));
        Assert.True(bake.GetProfile("narrow").Graph.IsNodePassable(0, 1, 1));
        if (slope == 0) Assert.True(bake.GetProfile("narrow").AllowsSegment(-Vector3.UnitX, Vector3.UnitX));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThinWallsAndDoorwaysRetainDirectedProofs(bool door)
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        if (door)
        {
            world.AddStatic(new BoxShape(new(0.05f, 1, 1)), Pose.At(new(0.5f, 1, -1.5f)));
            world.AddStatic(new BoxShape(new(0.05f, 1, 1)), Pose.At(new(0.5f, 1, 1.5f)));
        }
        else world.AddStatic(new BoxShape(new(0.05f, 1, 2)), Pose.At(new(0.5f, 1, 0)));
        using var context = new GroundMoveContext((_, _) => 0, physics: world);
        using var capture = PhysicsNavBake.Capture(context, Options, _ => 0);
        GroundNavigationBake bake = SameBytes(capture, new NavBakeProfile("legacy", Tuning, default));
        GroundNavigation nav = bake.GetProfile("legacy");
        Assert.True(nav.Graph.IsNodePassable(0, 1, 0));
        Assert.True(nav.Graph.IsNodePassable(0, 2, 0));
        Assert.Equal(door, nav.AllowsSegment(Vector3.Zero, Vector3.UnitX));
        Assert.Equal(door, nav.AllowsSegment(Vector3.UnitX, Vector3.Zero));
    }

    [Fact]
    public void StackedSurfacesRetainStairCandidatesAndDirectedLinks()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        world.AddStatic(new BoxShape(new(2, 0.05f, 2)), Pose.At(new(-2, 0.05f, 0)));
        world.AddStatic(new BoxShape(new(2, 0.025f, 2)), Pose.At(new(2, 0.275f, 0)));
        using var context = new GroundMoveContext((_, _) => 0, physics: world);
        using var capture = PhysicsNavBake.Capture(context, Options with { MinX = -1, MaxX = 1 }, _ => 0);
        MoveTuning tiny = Tuning with { CapsuleRadius = 0.04f, CapsuleHalfHeight = 0.1f, MaxStepClimbSpeed = 0 };
        GroundNavigationBake bake = SameBytes(capture, new NavBakeProfile("legacy", tiny, default));
        Assert.NotEmpty(bake.GetProfile("legacy").Graph.Links);
        Assert.All(bake.GetProfile("legacy").Graph.Links, link => Assert.Equal(NavLinkKind.Stair, link.Kind));
    }

    [Fact]
    public void MissingAndPaddedColumnsRetainTheirRefusals()
    {
        using var world = new BepuPhysicsWorld();
        world.AddStatic(new BoxShape(new(0.4f, 0.1f, 1)), Pose.At(new(-1, -0.1f, 0)));
        using var context = new GroundMoveContext((_, _) => 0, physics: world);
        using var capture = PhysicsNavBake.Capture(context, Options with { MaxX = 0.7f }, _ => 1);
        GroundNavigation nav = SameBytes(capture, new NavBakeProfile("legacy", Tuning, default)).GetProfile("legacy");
        Assert.True(nav.AllowsSegment(-Vector3.UnitX, -Vector3.UnitX));
        Assert.False(nav.AllowsSegment(-Vector3.UnitX, Vector3.Zero));
        Assert.False(nav.AllowsSegment(-Vector3.UnitX, Vector3.UnitX));
    }

    [Fact]
    public void AreaFootprintsRetainAllRequiredAndForbiddenBits()
    {
        using var world = GroundTraversalProbeTests.FlatWorld();
        using var context = new GroundMoveContext((_, _) => 0, physics: world);
        using var capture = PhysicsNavBake.Capture(context, Options with { MinZ = -1.5f, MaxZ = 1.5f },
            feet => feet.X >= 1 ? 7u : 3u);
        var filter = new NavAreaFilter(3, 4);
        GroundNavigationBake bake = SameBytes(capture, new("narrow", Tuning, filter),
            new("wide", Tuning with { CapsuleRadius = 0.6f }, filter));
        Assert.True(bake.GetProfile("narrow").Graph.IsNodePassable(0, 1, 1));
        Assert.False(bake.GetProfile("wide").Graph.IsNodePassable(0, 1, 1));
    }

    [Fact]
    public void WaterAndAquaticFallbackRetainCanonicalBytes()
    {
        using var world = SwimTraversalProbeTests.PoolWorld();
        using var context = SwimTraversalProbeTests.PoolContext(world);
        using var capture = PhysicsNavBake.Capture(context, Options with
        {
            MinX = -3, MaxX = 3, MinZ = -3, MaxZ = 3, MaxCells = 64, MaxLayerCells = 128, SampleWater = true,
        }, feet => feet.Y < -1 ? 2u : 1u);
        MoveTuning duck = SwimTraversalProbeTests.Duck;
        GroundNavigationBake bake = SameBytes(capture, new("dry", duck, default),
            new NavBakeProfile("aquatic", duck, default) { Aquatic = true });
        GroundNavigation aquatic = bake.GetProfile("aquatic");
        Assert.Contains(aquatic.Graph.Layers, layer => layer.IsAccepted(2, 2));
        Assert.True(aquatic.AllowsSegment(new(-0.5f, -0.3f, -0.5f), new(0.5f, -0.3f, -0.5f)));
        Assert.Contains(aquatic.Graph.Layers, layer => layer.IsAccepted(0, 0));
    }

    private static GroundNavigationBake SameBytes(PhysicsNavBake capture, params NavBakeProfile[] profiles)
    {
        NavBakeSources sources = ContactBakeFile.Sources();
        GroundNavigationBake baseline = GroundNavigationBake.Create(capture, sources, profiles);
        GroundNavigationBake scoped = GroundNavigationBake.Create(capture, sources, profiles, Stable());
        byte[] expected = ContactBakeFile.Write(baseline);
        byte[] actual = ContactBakeFile.Write(scoped);
        Assert.Equal(expected, actual);
        GroundNavigationBake loaded = ContactBakeFile.Load(actual, new(capture.Options, sources, profiles));
        Assert.Equal(actual, ContactBakeFile.Write(loaded));
        return scoped;
    }
}
