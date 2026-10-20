using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class CertifiedMeshSweepLifetimeTests
{
    [Fact]
    public void MeshRoundedScaleWarmQueriesAllocateNothing()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var pose = new Pose(new(3, 2, -4), new(0, 0, 0.5f, 0.8660254f));
        StaticHandle handle = world.AddStatic(CertifiedMeshSweepTests.Quad(), pose);
        CertifiedMeshSweepCoverageTests.SetScale(world, handle, new(2, 0.5f, 1.5f));
        Vector3 start = pose.Position + Vector3.Transform(new Vector3(-1, 2, -0.5f), pose.Orientation);
        Vector3 delta = new(2, 0, 0);
        var dimensions = new CapsuleQueryDimensions(0.25f, 1);
        var shape = new CapsuleShape(0.25f, 1);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        CapsuleSweepResult expected = world.SweepCapsuleCertified(dimensions, Pose.At(start), delta);
        Assert.Equal(CapsuleSweepStatus.Hit, expected.Status);
        SameBits(expected, world.SweepCapsuleCertified(shape, Pose.At(start), delta));
        CapsuleSweepResult value = default, shaped = default;
        long bytes = AllocationMeasurement.Measure(nameof(MeshRoundedScaleWarmQueriesAllocateNothing), () =>
        {
            value = world.SweepCapsuleCertified(dimensions, Pose.At(start), delta);
            shaped = world.SweepCapsuleCertified(shape, Pose.At(start), delta);
        });
        SameBits(expected, value); SameBits(expected, shaped);
        Assert.Equal(0, bytes);
        lease.AssertCurrent();
    }

    [Fact]
    public void MeshMutationDimensionAndWarmExactQueriesAllocateNothing()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle handle = world.AddStatic(CertifiedMeshSweepTests.Quad(), Pose.Identity);
        var previousNative = Static(world, handle);
        var captures = (System.Collections.Generic.Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics");
        InstalledSweepGeometry previousGeometry = captures[handle.Value]!;
        Vector3 start = new(-0.5f, 2, -0.25f), delta = new(0, -4, 0);
        HitContains(Read(world, start, delta), 1.25, 1.25);
        world.Rebase(new(16, 8, -16));
        HitContains(Read(world, start - world.Origin, delta), 1.25, 1.25);
        world.Rebase(world.Origin);
        world.RemoveStatic(handle);
        Clear(Read(world, start - world.Origin, delta), 4);
        StaticHandle replacement = world.AddStatic(new TriangleMeshShape(
            [new(-3, 0, -3), new(3, 0, -3), new(-3, 0, 3), new(3, 0, 3)], [0, 1, 2, 1, 3, 2]), Pose.At(-world.Origin));
        var reused = Static(world, replacement);
        Assert.Equal(previousNative.Handle.Value, reused.Handle.Value);
        Assert.Equal(previousNative.Shape.Index, reused.Shape.Index);
        Assert.False(captures.ContainsKey(handle.Value));
        Assert.NotSame(previousGeometry, captures[replacement.Value]);
        Assert.NotEqual(previousGeometry.Mesh!.Triangle(0).Vertex(0), captures[replacement.Value]!.Mesh!.Triangle(0).Vertex(0));
        HitContains(Read(world, start - world.Origin, delta), 1.25, 1.25);
        InstalledSweepGeometry currentGeometry = captures[replacement.Value]!;
        captures[replacement.Value] = previousGeometry;
        Refused(Read(world, start - world.Origin, delta));
        captures[replacement.Value] = currentGeometry;
        HitContains(Read(world, start - world.Origin, delta), 1.25, 1.25);
        using var excluded = world.CreateQueryViewExcludingStatics([replacement]);
        using var included = world.CreateQueryViewExcludingStatics([]);
        using (IPhysicsQueryLease viewLease = ((IPhysicsQueryLeaseSource)excluded).AcquireQueryReadLease())
        {
            var dimensions = new CapsuleQueryDimensions(0.25f, 1);
            Clear(((IPhysicsCapsuleGeometryQueries)excluded).SweepCapsuleCertified(dimensions, Pose.At(start - world.Origin), delta), 4);
            HitContains(((IPhysicsCapsuleGeometryQueries)included).SweepCapsuleCertified(dimensions, Pose.At(start - world.Origin), delta), 1.25, 1.25);
            Clear(world.SweepCapsuleCertified(dimensions, Pose.At(start - world.Origin), delta, QueryFilter.DynamicsOnly), 4);
            viewLease.AssertCurrent();
        }
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        foreach ((float radius, float length) in new[] { (0.4f, 0.59999996f), (0.40000007f, 0.6000001f), (0.25f, 0f) })
        {
            var dimensions = new CapsuleQueryDimensions(radius, length);
            var shape = new CapsuleShape(radius, length);
            Pose query = Pose.At(start - world.Origin);
            CapsuleSweepResult expected = world.SweepCapsuleCertified(shape, query, delta);
            Assert.Equal(CapsuleSweepStatus.Hit, expected.Status);
            SameBits(expected, world.SweepCapsuleCertified(dimensions, query, delta));
            Pose edge = Pose.At(new Vector3(0, 0.25f, 0) - world.Origin);
            var exactEdge = new CapsuleQueryDimensions(0.25f, 0);
            CapsuleSweepResult tangent = world.SweepCapsuleCertified(exactEdge, edge, Vector3.Zero);
            HitContains(tangent, 0, 0);
            var refused = dimensions;
            CapsuleSweepResult measured = default, value = default, boundary = default, failure = default;
            Refused(world.SweepCapsuleCertified(refused, query, delta, QueryFilter.All with { Layers = 1 }));
            long bytes = AllocationMeasurement.Measure(nameof(MeshMutationDimensionAndWarmExactQueriesAllocateNothing), () =>
            {
                measured = world.SweepCapsuleCertified(shape, query, delta);
                value = world.SweepCapsuleCertified(dimensions, query, delta);
                boundary = world.SweepCapsuleCertified(exactEdge, edge, Vector3.Zero);
                failure = world.SweepCapsuleCertified(refused, query, delta, QueryFilter.All with { Layers = 1 });
            });
            SameBits(expected, measured); SameBits(expected, value); SameBits(tangent, boundary);
            Refused(failure);
            Assert.Equal(0, bytes);
        }
        var native = Static(world, replacement);
        Sim(world).Statics.GetDescription(native.Handle, out var description);
        var geometry = ((System.Collections.Generic.Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics"))[replacement.Value]!;
        var shapes = Sim(world).Shapes;
        var boundedScratch = new CapsuleSweepScratch();
        var boundedDimensions = new CapsuleQueryDimensions(0.25f, 1);
        Vector3 touch = new Vector3(-0.5f, 0.75f, -0.25f) - world.Origin;
        var account = new CapsuleSweepWork();
        HitContains(CapsuleMeshSweep.Sweep(shapes, native.Shape, description.Pose, geometry,
            boundedDimensions, touch, Vector3.Zero, default, ref account, boundedScratch), 0, 0);
        account = default;
        Refused(CapsuleMeshSweep.SweepBounded(shapes, native.Shape, description.Pose, geometry,
            boundedDimensions, touch, Vector3.Zero, default, ref account, boundedScratch, 8192, 1, 128));
        CapsuleSweepResult bounded = default;
        long refusedBytes = AllocationMeasurement.Measure(nameof(MeshMutationDimensionAndWarmExactQueriesAllocateNothing) + "_bounded", () =>
        {
            account = default;
            bounded = CapsuleMeshSweep.SweepBounded(shapes, native.Shape, description.Pose, geometry,
                boundedDimensions, touch, Vector3.Zero, default, ref account, boundedScratch, 8192, 1, 128);
        });
        Refused(bounded);
        Assert.True(account.Exhausted);
        Assert.Equal(0, refusedBytes);
        lease.AssertCurrent();
    }
}
