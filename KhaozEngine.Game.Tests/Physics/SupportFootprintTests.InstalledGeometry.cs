using System;
using System.Linq;
using System.Numerics;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void InstalledLocalTopologyIsValidatedOnceAndWorldResultsStayIdentical()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1, 0)));
        using var lease = world.AcquireQueryReadLease();
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        int validations = 0;
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            if (phase == ContactAllocationPhase.SupportLocalValidation && entering) validations++;
        });
        try
        {
            var cold = Capability(world).QuerySupportFootprint(lease, Request(), members, joins);
            Assert.Equal(CapsuleFeatureStatus.Complete, cold.Status);
            Assert.True(validations > 0);
            var savedMembers = members.Take(cold.Members).ToArray();
            var savedJoins = joins.Take(cold.Members * cold.JoinWordsPerRow).ToArray();
            validations = 0;
            var warm = Capability(world).QuerySupportFootprint(lease, Request(), members, joins);
            Assert.Equal(cold, warm);
            Assert.Equal(savedMembers, members.Take(warm.Members));
            Assert.Equal(savedJoins, joins.Take(warm.Members * warm.JoinWordsPerRow));
            Assert.Equal(0, validations);
            Capability(world).QuerySupportFootprint(lease, Request(x: 0.15f), members, joins);
            var replay = Capability(world).QuerySupportFootprint(lease, Request(), members, joins);
            Assert.Equal(savedMembers, members.Take(replay.Members));
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
    }

    [Fact]
    public void InstalledLocalGeometryOwnsArraysAndMatchesTheUncachedInstalledOperator()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var owner = world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.Identity);
        var simulation = SimulationOf(world);
        var shape = HandlesOf(world)[owner.Value].Shape;
        var cache = new InstalledSupportGeometryCache();
        Assert.Equal(CapsuleFeatureStatus.Complete, cache.GetOrCapture(simulation, owner, shape, 1, out var geometry));
        var scratch = new CapsuleFeatureCaptureScratch();
        var leaves = new System.Collections.Generic.List<CapsuleFeaturePolyhedron>();
        var curved = new System.Collections.Generic.List<SupportCurvedLeaf>();
        var pose = new RigidPose(new Vector3(0.25f, -0.5f, 0.125f), Quaternion.CreateFromYawPitchRoll(0.37f, 0.25f, -0.4f));
        Assert.Equal(CapsuleFeatureStatus.Complete, geometry!.Leaves[0].Materialize(pose, scratch, leaves, curved));
        Assert.Equal(CapsuleFeatureStatus.Complete, CapsuleFeatureGeometry.CaptureLeaf(simulation, shape, pose,
            new RigidPose(Vector3.Zero, Quaternion.Identity), 0, out var uncached));
        Assert.Equal(uncached!.Vertices.Select(v => v.Bounds), leaves[0].Vertices.Select(v => v.Bounds));
        Assert.Equal(uncached.Normals.Select(v => v.Bounds), leaves[0].Normals.Select(v => v.Bounds));
        leaves[0].LocalVertices[0] = new(999, 999, 999);
        leaves[0].Faces[0][0] = 7;
        leaves.Clear();
        scratch.Reset();
        Assert.Equal(CapsuleFeatureStatus.Complete, geometry.Leaves[0].Materialize(pose, scratch, leaves, curved));
        Assert.Equal(new Vector3(-2, -1, -2), leaves[0].LocalVertices[0]);
        Assert.Equal(0, leaves[0].Faces[0][0]);
        Assert.Equal(uncached.Vertices.Select(v => v.Bounds), leaves[0].Vertices.Select(v => v.Bounds));
        var privateFaces = new InstalledSupportLeaf(0, Pose.Identity, uncached.LocalVertices, uncached.Faces,
            null, 0, 0, shape);
        uncached.Faces[0][0] = 7;
        leaves.Clear();
        scratch.Reset();
        Assert.Equal(CapsuleFeatureStatus.Complete, privateFaces.Materialize(pose, scratch, leaves, curved));
        Assert.Equal(0, leaves[0].Faces[0][0]);

        Vector3[] vertices = [Vector3.UnitX];
        int[][] faces = [[0]];
        var privatelyOwned = new InstalledSupportLeaf(0, Pose.Identity, vertices, faces, null, 0, 0);
        vertices[0] = Vector3.UnitZ;
        faces[0][0] = 1;
        Assert.Equal(Vector3.UnitX, privatelyOwned.Vertices[0]);
    }

    [Fact]
    public void InstalledLocalCacheSeparatesOwnerInstallationAndStaticVersionAndEvictsExplicitly()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var first = world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.Identity);
        var second = world.AddStatic(new BoxShape(new(3, 2, 3)), Pose.Identity);
        var simulation = SimulationOf(world);
        var firstShape = HandlesOf(world)[first.Value].Shape;
        var secondShape = HandlesOf(world)[second.Value].Shape;
        var cache = new InstalledSupportGeometryCache();
        Assert.Equal(CapsuleFeatureStatus.Complete, cache.GetOrCapture(simulation, first, firstShape, 1, out var cold));
        Assert.Equal(CapsuleFeatureStatus.Complete, cache.GetOrCapture(simulation, first, firstShape, 1, out var warm));
        Assert.Same(cold, warm);
        Assert.Equal(CapsuleFeatureStatus.Complete, cache.GetOrCapture(simulation, second, firstShape, 1, out var otherOwner));
        Assert.NotSame(cold, otherOwner);
        Assert.Equal(CapsuleFeatureStatus.Complete, cache.GetOrCapture(simulation, first, secondShape, 1, out var replacement));
        Assert.Equal(new Vector3(-3, -2, -3), replacement!.Leaves[0].Vertices[0]);
        Assert.Equal(CapsuleFeatureStatus.Complete, cache.GetOrCapture(simulation, first, secondShape, 2, out var nextVersion));
        Assert.NotSame(replacement, nextVersion);
        cache.Remove(first);
        Assert.Equal(CapsuleFeatureStatus.Complete, cache.GetOrCapture(simulation, first, secondShape, 2, out var afterRemoval));
        Assert.NotSame(nextVersion, afterRemoval);
        cache.Clear();
        Assert.Equal(CapsuleFeatureStatus.Complete, cache.GetOrCapture(simulation, first, secondShape, 2, out var afterRebase));
        Assert.NotSame(afterRemoval, afterRebase);
    }

    [Fact]
    public void WarmInstalledGeometryRechecksSelectedExclusionsAndRebasesBeforeReuse()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var owner = world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1, 0)));
        var members = new SupportFootprintMember[256];
        var joins = new ulong[1024];
        using var excluded = world.CreateQueryViewExcludingStatics([owner]);
        using (var lease = world.AcquireQueryReadLease())
        {
            Assert.Equal(1, Capability(world).QuerySupportFootprint(lease, Request(), members, joins).Members);
            Assert.Equal(0, Capability(excluded).QuerySupportFootprint(lease, Request(), members, joins).Members);
            Assert.Equal(1, Capability(world).QuerySupportFootprint(lease, Request(), members, joins).Members);
        }
        world.Rebase(new(0, 0.125f, 0));
        int validations = 0;
        ContactAllocationDiagnostics.SetObserver((phase, entering) =>
        {
            if (phase == ContactAllocationPhase.SupportLocalValidation && entering) validations++;
        });
        try
        {
            using var lease = world.AcquireQueryReadLease();
            var rebased = Capability(world).QuerySupportFootprint(lease, Request(), members, joins);
            Assert.Equal(CapsuleFeatureStatus.Complete, rebased.Status);
            Assert.True(validations > 0);
            Assert.Equal(-0.125, members.Take(rebased.Members).Single(m => m.HasDiscMaximum).DiscHeightUpper);
        }
        finally { ContactAllocationDiagnostics.SetObserver(null); }
        world.RemoveStatic(owner);
        var replacement = world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -0.875f, 0)));
        using var next = world.AcquireQueryReadLease();
        var replaced = Capability(world).QuerySupportFootprint(next, Request(), members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, replaced.Status);
        Assert.All(members.Take(replaced.Members), m => Assert.Equal(replacement, m.Static));
        Assert.Equal(0.125, members.Take(replaced.Members).Single(m => m.HasDiscMaximum).DiscHeightUpper);
    }
}
