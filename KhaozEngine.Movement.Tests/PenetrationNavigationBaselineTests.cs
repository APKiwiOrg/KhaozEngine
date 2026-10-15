using System;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using Xunit.Abstractions;

namespace KhaozEngine.Tests.Movement;

/// <summary>A nine-cell legacy bake pins query-view penetration behavior through canonical output bytes.</summary>
public sealed class PenetrationNavigationBaselineTests(ITestOutputHelper output)
{
    [Fact]
    public void ExcludedGroundAndEligibleWallKeepCanonicalBakeBytes()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle floor = world.AddStatic(new BoxShape(new Vector3(4f, 0.25f, 4f)),
            Pose.At(new Vector3(0f, -0.25f, 0f)));
        world.AddStatic(new BoxShape(new Vector3(0.05f, 1f, 2f)), Pose.At(new Vector3(0.5f, 1f, 0f)));
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([floor]);
        using var context = new GroundMoveContext((_, _) => 0f, null, world, null, null, view);
        var tuning = GroundTraversalProbeTests.Tuning;
        var capsule = new CapsuleShape(tuning.CapsuleRadius, 2f * (tuning.CapsuleHalfHeight - tuning.CapsuleRadius));
        Pose floorOverlap = Pose.At(new Vector3(-1f, 0.5f, 0f));
        Assert.True(world.ComputePenetration(capsule, floorOverlap, out _));
        Assert.False(view.ComputePenetration(capsule, floorOverlap, out _));
        Assert.True(view.ComputePenetration(capsule, Pose.At(new Vector3(0.4f, 0.75f, 0f)), out _));

        var options = new PhysicsNavBakeOptions(-1.5f, -1.5f, 1.5f, 1.5f, 1f, 5f, 6f, 0.8f, 9, 16);
        NavBakeProfile[] profiles = [new("player", tuning, default)];
        var sources = new NavBakeSources().AddHashOf("fixture", "excluded-ground-eligible-wall-v1"u8);
        using PhysicsNavBake capture = PhysicsNavBake.Capture(context, options, _ => 0u);
        GroundNavigationBake bake = GroundNavigationBake.Create(capture, sources, profiles);
        GroundNavigation nav = bake.GetProfile("player");
        Assert.Equal(3, nav.Space.Layers[0].Width);
        Assert.Equal(3, nav.Space.Layers[0].Height);
        Assert.True(nav.Graph.IsNodePassable(0, 1, 1));
        Assert.True(nav.Graph.IsNodePassable(0, 2, 1));
        Assert.True(nav.Graph.CanTraverse(0, 0, 1, 0, 1, 1));
        Assert.False(nav.Graph.CanTraverse(0, 1, 1, 0, 2, 1));

        byte[] file = ContactNavigationBaselineTests.NormalizeFile(new(bake, new(options, sources, profiles)));
        string hash = Convert.ToHexStringLower(SHA256.HashData(file));
        output.WriteLine($"Canonical bake ({file.Length} bytes): {hash}");
        // Captured on the unchanged backend before lazy batcher initialization. Only engine identity is normalized.
        Assert.Equal("8dc879897a4cdfed8602306ff7fc9d7c6d316988270a01de71ee5c55a523bc17", hash);
    }
}
