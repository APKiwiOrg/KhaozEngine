using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using BepuPhysics;
using BepuPhysics.Collidables;
using BepuUtilities.Memory;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using StaticHandle = KhaozEngine.Physics.StaticHandle;

namespace KhaozEngine.Tests.Physics;

public sealed partial class StaticMutationLeaseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticEditsInvalidateInstalledSupportAndCertifiedSweepEvidence(bool leased)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle floor = world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -1, 0)));
        AssertSupport(floor, 0);
        int captures = SupportCaptures(world);
        AssertSupport(floor, 0);
        Assert.Equal(captures, SupportCaptures(world));
        Assert.Equal(CapsuleSweepStatus.Clear, Sweep(world).Status);

        using (IPhysicsStaticMutationLease? writer = leased ? Acquire(world) : null)
        {
            world.RemoveStatic(floor);
            floor = world.AddStatic(new BoxShape(new(2, 1, 2)), Pose.At(new(0, -0.875f, 0)));
            world.AddStatic(Box, Pose.At(new(2, 2, 0)));
            writer?.AssertCurrent();
        }

        AssertSupport(floor, 0.125);
        Assert.True(SupportCaptures(world) > captures);
        Assert.Equal(CapsuleSweepStatus.Hit, Sweep(world).Status);
        using (IPhysicsStaticMutationLease? writer = leased ? Acquire(world) : null)
            world.RemoveStatic(floor);
        using IPhysicsQueryLease read = world.AcquireQueryReadLease();
        var members = new SupportFootprintMember[16];
        var joins = new ulong[16];
        SupportFootprintResult absent = world.QuerySupportFootprint(read, FootprintRequest(), members, joins);
        Assert.Equal(CapsuleFeatureStatus.Complete, absent.Status);
        Assert.Equal(0, absent.Members);

        void AssertSupport(StaticHandle expected, double height)
        {
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            var members = new SupportFootprintMember[16];
            var joins = new ulong[16];
            SupportFootprintResult result = world.QuerySupportFootprint(lease, FootprintRequest(), members, joins);
            Assert.Equal(CapsuleFeatureStatus.Complete, result.Status);
            SupportFootprintMember member = Assert.Single(members.Take(result.Members));
            Assert.Equal(expected, member.Static);
            Assert.True(member.HasDiscMaximum);
            Assert.Equal(height, member.DiscHeightUpper);
            world.AssertFootprintCurrent(result, lease);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedStaticInstallationRetainsConservativeInvalidationAndReleasesItsInterval(bool leased)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Assert.Equal(CapsuleSweepStatus.Clear, Sweep(world).Status);
        long geometry = ReadGeneration(world), statics = world.StaticGeometryGeneration;

        Assert.Throws<NotSupportedException>(() =>
        {
            using IPhysicsStaticMutationLease? writer = leased ? Acquire(world) : null;
            world.AddStatic(new UnsupportedShape(), Pose.Identity);
        });

        Assert.Equal(geometry + 1, ReadGeneration(world));
        Assert.Equal(statics + 1, world.StaticGeometryGeneration);
        Assert.Equal(CapsuleSweepStatus.Unresolved, Sweep(world).Status);
        LeaseTestThread.Start(() => world.AddStatic(Box, Pose.At(new(2, 2, 0)))).Join();
        // A later successful edit must not heal failed installation evidence.
        Assert.Equal(CapsuleSweepStatus.Unresolved, Sweep(world).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovingOwnedShapesReleasesRootChildAndMeshStorage(bool mesh)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        PhysicsShape shape = mesh
            ? new TriangleMeshShape([new(0, -1, -1), new(0, 1, -1), new(0, 0, 1)], [0, 1, 2])
            : new CompoundShape([new(Box, Pose.Identity)]);
        using IPhysicsStaticMutationLease writer = Acquire(world);
        StaticHandle first = world.AddStatic(shape, Pose.At(new(2, 0, 0)));
        TypedIndex root = InstalledShape(world, first);
        Simulation simulation = Field<Simulation>(world, "_sim");
        int? child = mesh ? null : simulation.Shapes.GetShape<Compound>(root.Index).Children[0].ShapeIndex.Index;
        int installedStorage = OutstandingStorage(world);
        Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 4, out _));

        world.RemoveStatic(first);
        Assert.False(world.Raycast(Vector3.Zero, Vector3.UnitX, 4, out _));
        Assert.True(OutstandingStorage(world) < installedStorage);
        StaticHandle second = world.AddStatic(shape, Pose.At(new(2, 0, 0)));
        TypedIndex replacement = InstalledShape(world, second);
        Assert.Equal(root, replacement);
        Assert.Equal(installedStorage, OutstandingStorage(world));
        if (child is int originalChild)
            Assert.Equal(originalChild, simulation.Shapes.GetShape<Compound>(replacement.Index).Children[0].ShapeIndex.Index);
        Assert.True(world.Raycast(Vector3.Zero, Vector3.UnitX, 4, out RayHit hit));
        Assert.Equal(second, hit.Body);
        world.RemoveStatic(second);
        writer.AssertCurrent();
    }

    static CapsuleSweepResult Sweep(BepuPhysicsWorld world)
    {
        using IPhysicsQueryLease read = world.AcquireQueryReadLease();
        return world.SweepCapsuleCertified(new CapsuleShape(0.25f, 0.5f),
            Pose.At(new(0, 2, 0)), new(4, 0, 0), QueryFilter.StaticsOnly);
    }

    static SupportFootprintRequest FootprintRequest() => new(Vector2.Zero, 0.2f, -0.4, 0.4,
        0.70710677f, 0.0001f,
        new(new CapsuleShape(0.01f, 0.01f), Pose.At(new(0, 0.416f, 0)), 0.802f),
        new(new CapsuleShape(0.2f, 0.01f), Pose.At(new(0, 0.606f, 0)), 0.802f));

    static int SupportCaptures(BepuPhysicsWorld world)
    {
        object cache = Field<object>(world, "_installedSupportGeometry");
        return (int)cache.GetType().GetProperty("Constructions", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(cache)!;
    }

    static TypedIndex InstalledShape(BepuPhysicsWorld world, StaticHandle handle) =>
        Field<Dictionary<int, (BepuPhysics.StaticHandle, TypedIndex)>>(world, "_handles")[handle.Value].Item2;

    static int OutstandingStorage(BepuPhysicsWorld world)
    {
        // Retained block capacity is not a leak. Count slots still loaned by the backend allocator,
        // independently of which returned buffer a subsequent mesh construction happens to reuse.
        var pools = (Array)typeof(BufferPool).GetField("pools", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(Field<BufferPool>(world, "_pool"))!;
        return pools.Cast<object>().Sum(pool =>
        {
            var slots = (ManagedIdPool)pool.GetType().GetField("Slots")!.GetValue(pool)!;
            return slots.HighestPossiblyClaimedId + 1 - slots.AvailableIdCount;
        });
    }

    static T Field<T>(BepuPhysicsWorld world, string name) =>
        (T)typeof(BepuPhysicsWorld).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;

    sealed class UnsupportedShape : PhysicsShape;
}
