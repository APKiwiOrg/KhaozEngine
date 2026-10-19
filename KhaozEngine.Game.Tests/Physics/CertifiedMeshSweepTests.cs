using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class CertifiedMeshSweepTests
{
    internal static TriangleMeshShape Quad(bool reverse = false) => new(
        [new(-2, 0, -2), new(2, 0, -2), new(-2, 0, 2), new(2, 0, 2)],
        reverse ? [0, 2, 1, 1, 2, 3] : [0, 1, 2, 1, 3, 2]);

    [Fact]
    public void MeshFrontFaceEntryHasAContainingMetreBracket()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Quad(), Pose.Identity);
        Vector3 centre = new(-0.5f, 2, -0.25f), delta = new(0, -4, 0);
        Assert.True(world.SweepCapsule(new(0.25f, 1), Pose.At(centre), -Vector3.UnitY, 4, out _, QueryFilter.StaticsOnly));
        foreach (bool cull in new[] { false, true })
            HitContains(Read(world, centre, delta, filter: QueryFilter.StaticsOnly with { CullBackFaces = cull }), 1.25, 1.25);
    }

    [Fact]
    public void MeshSideAndStationaryEligibilityUsePinnedPolicy()
    {
        using var front = new BepuPhysicsWorld(Vector3.Zero);
        using var back = new BepuPhysicsWorld(Vector3.Zero);
        front.AddStatic(Quad(), Pose.Identity);
        back.AddStatic(Quad(true), Pose.Identity);
        QueryFilter culling = QueryFilter.StaticsOnly with { CullBackFaces = true };
        Vector3 start = new(-0.5f, 2, -0.25f), down = new(0, -4, 0);
        Assert.False(back.SweepCapsule(new(0.25f, 1), Pose.At(start), -Vector3.UnitY, 4, out _, culling));
        Assert.True(back.SweepCapsule(new(0.25f, 1), Pose.At(start), -Vector3.UnitY, 4, out _, QueryFilter.StaticsOnly));
        Clear(Read(back, start, down, filter: culling), 4);
        HitContains(Read(back, start, down, filter: QueryFilter.StaticsOnly), 1.25, 1.25);
        foreach (bool cull in new[] { false, true })
        {
            QueryFilter filter = QueryFilter.StaticsOnly with { CullBackFaces = cull };
            foreach (Vector3 delta in new[] { Vector3.Zero, new Vector3(0.125f, 0, 0), new Vector3(0, 0.125f, 0) })
            {
                CapsuleSweepResult stationary = Read(front, new(-0.5f, 0.75f, -0.25f), delta, filter: filter);
                HitContains(stationary, 0, 0);
                Assert.Equal(0f, stationary.ImpactDistance);
            }
            Clear(Read(front, new(-0.5f, 0.875f, -0.25f), Vector3.Zero, filter: filter), 0);
            Clear(Read(front, new(-0.5f, 0.875f, -0.25f), new(0.125f, 0, 0), filter: filter), 0.125f);
            HitContains(Read(front, new(-0.5f, 0.625f, -0.25f), Vector3.Zero, filter: filter), 0, 0);
            HitContains(Read(front, new(-0.5f, 1.75f, -0.25f), new(0, -1, 0), filter: filter), 1, 1);
        }
        using var vertical = new BepuPhysicsWorld(Vector3.Zero);
        Vector3[] wall = [new(-1, 0, 0), new(1, 0, 0), new(-1, -1, 0), new(1, -1, 0)];
        Assert.Equal(new Vector3(0, 0, 2), Vector3.Cross(wall[2] - wall[0], wall[1] - wall[0]));
        vertical.AddStatic(new TriangleMeshShape(wall, [0, 1, 2, 1, 3, 2]), Pose.Identity);
        Clear(Read(vertical, new(0, 1, 0), new(0, -2, 0), filter: culling), 2);
    }

    [Fact]
    public void MeshFiniteEdgeVertexAndThinInteriorTriangleAreNotLost()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle handle = world.AddStatic(new TriangleMeshShape([new(0, -2, -2), new(0, -2, 2), new(0, 2, -2)], [0, 1, 2]), Pose.Identity);
        Vector3 start = new(2, 0, -0.5f), delta = new(-4, 0, 0);
        HitContains(Read(world, start, delta), 1.75, 1.75);
        var face = CertifiedSweepExactOracle.TriangleEntry(world, handle, 0, start, 0.25f, 0.5f, delta);
        HitContains(Read(world, start, delta), face.Lower, face.Upper);
        using var boundary = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle boundaryHandle = boundary.AddStatic(new TriangleMeshShape(
            [new(0, -2, -2), new(0, -2, 2), new(0, 4, 0)], [0, 1, 2]), Pose.Identity);
        foreach (Vector3 point in new[] { new Vector3(2, -2.25f, 0), new(2, -2.25f, -2) })
        {
            var expected = CertifiedSweepExactOracle.TriangleEntry(boundary, boundaryHandle, 0, point, 0.25f, 0, delta);
            Assert.Equal((2d, 2d), expected);
            HitContains(Read(boundary, point, delta, length: 0), expected.Lower, expected.Upper);
        }
        using var thin = new BepuPhysicsWorld(Vector3.Zero);
        thin.AddStatic(new TriangleMeshShape([new(0, -0.0078125f, -0.0078125f), new(0, -0.0078125f, 0.0078125f), new(0, 0.0078125f, -0.0078125f)], [0, 1, 2]), Pose.Identity);
        Clear(Read(thin, new(2, 0, -0.00390625f), Vector3.Zero), 0);
        Clear(Read(thin, new(-2, 0, -0.00390625f), Vector3.Zero), 0);
        HitContains(Read(thin, new(2, 0, -0.00390625f), delta), 1.75, 1.75);
    }

    [Fact]
    public void MeshUnprovedBackAndRecenteredBoundaryNeverBecomeAFalseClearOrHit()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new TriangleMeshShape([new(0, -2, -2), new(0, -2, 2), new(0, 2, -2)], [0, 1, 2]), Pose.Identity);
        // The rounded centroid continuation moves this isolated edge inward. The exact
        // original event exists at distance 2, but agreement has no event to enclose.
        Refused(Read(world, new(2, -2.25f, 0), new(-4, 0, 0), length: 0));
        using var back = new BepuPhysicsWorld(Vector3.Zero);
        back.AddStatic(Quad(true), Pose.Identity);
        Refused(Read(back, new(-0.5f, 0.75f, -0.25f), Vector3.Zero));
    }

    [Fact]
    public void MeshLaterUnprovedBoundaryCannotHideBehindAProvedFaceHit()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var pose = new Pose(new(3, 2, -4), new(0, 0, 0.5f, 0.8660254f));
        StaticHandle handle = world.AddStatic(Quad(), pose);
        CertifiedMeshSweepCoverageTests.SetScale(world, handle, new(2, 0.5f, 1.5f));
        Vector3 start = pose.Position + Vector3.Transform(new Vector3(-1, 2, -0.5f), pose.Orientation);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        var native = Static(world, handle);
        Sim(world).Statics.GetDescription(native.Handle, out var description);
        var geometry = ((System.Collections.Generic.Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics"))[handle.Value]!;
        Assert.True(geometry.TryPose(description.Pose, out var map));
        var leaf = new InstalledSweepLeaf(geometry.Mesh!.Triangle(0), description.Pose, map);
        var work = new CapsuleSweepWork();
        CapsuleSweepResult early = CapsuleTriangleSweep.Sweep(leaf, start, 0.25f, 0.5f, new(4, 0, 0), default,
            0.001f, ref work, new CapsuleSweepScratch());
        Assert.Equal(CapsuleSweepStatus.Hit, early.Status);
        Refused(world.SweepCapsuleCertified(new CapsuleQueryDimensions(0.25f, 1), Pose.At(start), new(4, 0, 0)));
        lease.AssertCurrent();
    }

    [Fact]
    public void MeshConcaveAndConvexSeamsKeepRawTriangleIdentity()
    {
        foreach (bool concave in new[] { false, true })
        {
            CapsuleSweepResult? baseline = null;
            foreach (bool reversed in new[] { false, true })
            {
                Vector3[] vertices = [new(-2, 0, -2), new(2, 0, -2), new(-2, 0, 2), new(2, 0, 2), new(-2, concave ? 2 : -2, -2), new(-2, concave ? 2 : -2, 2)];
                int[] indices = reversed ? [0, 2, 4, 4, 2, 5, 1, 3, 2, 0, 1, 2] : [0, 1, 2, 1, 3, 2, 4, 2, 5, 0, 2, 4];
                using var world = new BepuPhysicsWorld(Vector3.Zero);
                StaticHandle handle = world.AddStatic(new TriangleMeshShape(vertices, indices), Pose.Identity);
                var geometry = ((System.Collections.Generic.Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics"))[handle.Value]!;
                Assert.Equal(4, geometry.Mesh!.Count);
                for (int i = 0; i < 4; i++)
                    for (int j = i + 1; j < 4; j++) Assert.NotSame(geometry.Mesh.Triangle(i), geometry.Mesh.Triangle(j));
                CapsuleSweepResult result = Read(world, new(-2, 2, 0), new(0, -4, 0));
                HitContains(result, concave ? 0 : 1.25, concave ? 0 : 1.25);
                if (baseline is { } previous) SameBits(previous, result);
                baseline = result;
                using var separate = new BepuPhysicsWorld(Vector3.Zero);
                var floor = Quad();
                var wall = new TriangleMeshShape(vertices, [4, 2, 5, 0, 2, 4]);
                separate.AddStatic(reversed ? wall : floor, Pose.Identity);
                separate.AddStatic(reversed ? floor : wall, Pose.Identity);
                SameBits(result, Read(separate, new(-2, 2, 0), new(0, -4, 0)));
            }
        }
    }
}
