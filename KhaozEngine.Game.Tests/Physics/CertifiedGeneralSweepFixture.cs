using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using BepuPhysics.Collidables;
using BepuSim = BepuPhysics.Simulation;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

internal static class CertifiedGeneralSweepFixture
{
    internal static ConvexHullShape OverFaceCapHull()
    {
        // A closed 255-prism has 257 faces, beyond the fixed GS-3 face cap.
        var points = new Vector3[510];
        for (int i = 0; i < 255; i++)
        {
            float angle = i * (2 * MathF.PI / 255);
            float y = 8 * MathF.Cos(angle), z = 8 * MathF.Sin(angle);
            points[i * 2] = new(-1, y, z);
            points[i * 2 + 1] = new(1, y, z);
        }
        return new(points);
    }

    internal static StaticHandle AddOverFaceCapHull(BepuPhysicsWorld world, Pose pose)
    {
        StaticHandle handle = world.AddStatic(OverFaceCapHull(), pose);
        var shapes = Sim(world).Shapes;
        var root = Static(world, handle).Shape;
        var leaf = shapes.GetShape<Compound>(root.Index).Children[0].ShapeIndex;
        Assert.Equal(257, shapes.GetShape<ConvexHull>(leaf.Index).FaceToVertexIndicesStart.Length);
        return handle;
    }

    internal static CapsuleSweepResult Read(BepuPhysicsWorld world, Vector3 centre, Vector3 delta,
        float radius = 0.25f, float length = 1f, QueryFilter filter = default,
        bool useDimensions = false, StaticHandle[]? exclusions = null)
    {
        using IPhysicsWorldQueryView? view = exclusions is null ? null : world.CreateQueryViewExcludingStatics(exclusions);
        object receiver = (object?)view ?? world;
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)receiver).AcquireQueryReadLease();
        lease.AssertCurrent();
        CapsuleSweepResult result = useDimensions
            ? ((IPhysicsCapsuleGeometryQueries)receiver).SweepCapsuleCertified(new CapsuleQueryDimensions(radius, length),
                Pose.At(centre), delta, filter)
            : ((IPhysicsCapsuleSweep)receiver).SweepCapsuleCertified(new CapsuleShape(radius, length),
                Pose.At(centre), delta, filter);
        lease.AssertCurrent();
        return result;
    }

    internal static object Field(BepuPhysicsWorld world, string name) => typeof(BepuPhysicsWorld)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world)!;
    internal static BepuSim Sim(BepuPhysicsWorld world) => (BepuSim)Field(world, "_sim");
    internal static (BepuPhysics.StaticHandle Handle, TypedIndex Shape) Static(BepuPhysicsWorld world, StaticHandle handle) =>
        ((Dictionary<int, (BepuPhysics.StaticHandle, TypedIndex)>)Field(world, "_handles"))[handle.Value];
    internal static object? Call(BepuPhysicsWorld world, string name, params object?[] arguments) =>
        typeof(BepuPhysicsWorld).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(world, arguments);

    internal static void Mutate(BepuPhysicsWorld world, Action<BepuSim> mutation, int? changedStatic = null)
    {
        using var operation = (IDisposable)Call(world, "EnterMutation", false, true)!;
        object token = Call(world, "BeginSweepMutation")!;
        mutation(Sim(world));
        Call(world, "CompleteSweepMutation", token, changedStatic, false);
    }

    internal static StaticHandle Install(BepuPhysicsWorld world, BoxShape shape, Pose pose,
        Action<BepuSim, BepuPhysics.StaticHandle> beforePublication)
    {
        using var operation = (IDisposable)Call(world, "EnterStaticMutation")!;
        object token = Call(world, "BeginSweepMutation")!;
        BepuSim simulation = Sim(world);
        var pool = (BepuUtilities.Memory.BufferPool)Field(world, "_pool");
        TypedIndex index = ShapeFactory.Add(simulation, pool, shape);
        BepuPhysics.StaticHandle native = simulation.Statics.Add(new BepuPhysics.StaticDescription(pose.Position, pose.Orientation, index));
        FieldInfo next = typeof(BepuPhysicsWorld).GetField("_nextId", BindingFlags.Instance | BindingFlags.NonPublic)!;
        int id = (int)next.GetValue(world)!;
        next.SetValue(world, id + 1);
        Call(world, "AddStaticEntry", id, native, index);
        beforePublication(simulation, native);
        Call(world, "CompleteSweepMutation", token, id, false);
        return new(id);
    }

    internal static void Clear(CapsuleSweepResult result, float representedLength)
    {
        Assert.Equal(CapsuleSweepStatus.Clear, result.Status);
        Assert.Equal(representedLength, result.ClearThroughDistance);
        Assert.Null(result.ImpactDistance);
        Assert.InRange(result.CertifiedErrorMetres, 0f, 0.001f);
    }

    internal static void HitContains(CapsuleSweepResult result, double trueLower, double trueUpper)
    {
        Assert.Equal(CapsuleSweepStatus.Hit, result.Status);
        Assert.NotNull(result.ImpactDistance);
        Assert.True(result.ClearThroughDistance <= trueLower);
        Assert.True(trueLower <= trueUpper && trueUpper <= result.ImpactDistance.Value);
        Assert.InRange((double)result.ImpactDistance.Value - result.ClearThroughDistance, 0, result.CertifiedErrorMetres);
        Assert.InRange(result.CertifiedErrorMetres, 0f, 0.001f);
    }

    internal static void Refused(CapsuleSweepResult result) => Assert.Equal(default(CapsuleSweepResult), result);

    internal static void SameBits(CapsuleSweepResult expected, CapsuleSweepResult actual)
    {
        Assert.Equal(expected.Status, actual.Status);
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.ClearThroughDistance), BitConverter.SingleToInt32Bits(actual.ClearThroughDistance));
        Assert.Equal(BitConverter.SingleToInt32Bits(expected.CertifiedErrorMetres), BitConverter.SingleToInt32Bits(actual.CertifiedErrorMetres));
        Assert.Equal(expected.ImpactDistance.HasValue, actual.ImpactDistance.HasValue);
        if (expected.ImpactDistance is float impact)
            Assert.Equal(BitConverter.SingleToInt32Bits(impact), BitConverter.SingleToInt32Bits(actual.ImpactDistance!.Value));
    }
}
