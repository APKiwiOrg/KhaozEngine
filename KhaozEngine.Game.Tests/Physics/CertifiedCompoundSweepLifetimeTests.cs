using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using System.Threading;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;
using static KhaozEngine.Tests.Physics.CertifiedCompoundSweepTests;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class CertifiedCompoundSweepLifetimeTests
{
    static readonly Vector3 Start = new(-2, 0, 0), Delta = new(8, 0, 0);

    [Fact]
    public void CompoundMutationAndConcurrentScratchKeepInstallationLifetime()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var shape = new CompoundShape(Pair());
        var handle = world.AddStatic(shape, Pose.Identity);
        HitContains(Read(world, Start, Delta), 1.625, 1.625);
        var old = Static(world, handle);
        var oldCapture = Geometry(world, handle);
        world.Rebase(new(16, 8, -16));
        world.Rebase(world.Origin);
        HitContains(Read(world, Start - world.Origin, Delta), 1.625, 1.625);
        world.RemoveStatic(handle);
        handle = world.AddStatic(shape, Pose.At(-world.Origin));
        var replacement = Static(world, handle);
        Assert.Equal(old.Handle, replacement.Handle);
        Assert.Equal(old.Shape, replacement.Shape);
        Assert.NotSame(oldCapture, Geometry(world, handle));
        var captures = (Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics");
        var current = captures[handle.Value];
        captures[handle.Value] = oldCapture;
        Refused(Read(world, Start - world.Origin, Delta));
        captures[handle.Value] = current;
        HitContains(Read(world, Start - world.Origin, Delta), 1.625, 1.625);
        // Keep native geometry, root pose and registration id current. The independent
        // installation generation must reject a copied capture carrying an old generation.
        var generationField = typeof(InstalledSweepGeometry).GetField("_installationGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var registrationField = typeof(InstalledSweepGeometry).GetField("_registrationId", BindingFlags.NonPublic | BindingFlags.Instance)!;
        registrationField.SetValue(current, handle.Value - 1);
        Refused(Read(world, Start - world.Origin, Delta));
        registrationField.SetValue(current, handle.Value);
        long generation = (long)generationField.GetValue(current)!;
        generationField.SetValue(current, generationField.GetValue(oldCapture));
        Refused(Read(world, Start - world.Origin, Delta));
        generationField.SetValue(current, generation);
        HitContains(Read(world, Start - world.Origin, Delta), 1.625, 1.625);

        var body = world.AddDynamic(shape, Pose.At(new(40, 0, 0)), new DynamicBodyDescription(1));
        var bodies = (Dictionary<int, (BepuPhysics.BodyHandle Handle, TypedIndex Shape)>)Field(world, "_dynamics");
        Mutate(world, sim => { var reference = sim.Bodies[bodies[body.Value].Handle]; reference.Awake = false; });
        Assert.False(world.IsAwake(body));
        HitContains(Read(world, new(38, 0, 0), Delta, filter: QueryFilter.DynamicsOnly), 1.625, 1.625);
        world.SetDynamicVelocity(body, Vector3.UnitX, Vector3.Zero);
        Assert.True(world.IsAwake(body));
        world.Step(1f / 30);
        Pose pose = world.GetDynamicPose(body);
        HitContains(Read(world, pose.Position + Start, Delta, filter: QueryFilter.DynamicsOnly), 1.625, 1.625);
        var joint = world.AddConstraint(ConstraintDescription.HingeJoint(ConstraintAttachment.OnBody(body),
            ConstraintAttachment.AtWorld(pose.Position), Vector3.Zero, Vector3.Zero, Vector3.UnitY, Vector3.UnitY)
            with
        { Motor = ConstraintMotor.HingeVelocity, MotorTarget = 0 });
        world.SetConstraintTarget(joint, 0.5f);
        HitContains(Read(world, Start - world.Origin, Delta, filter: QueryFilter.StaticsOnly), 1.625, 1.625);
        world.RemoveConstraint(joint);
        var dynamicCaptures = (Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepDynamics");
        var oldDynamicCapture = dynamicCaptures[body.Value];
        var oldDynamic = bodies[body.Value];
        Pose replacementPose = world.GetDynamicPose(body);
        world.RemoveDynamic(body);
        body = world.AddDynamic(shape, replacementPose, new DynamicBodyDescription(1));
        Assert.Equal(oldDynamic, bodies[body.Value]);
        var currentDynamicCapture = dynamicCaptures[body.Value];
        dynamicCaptures[body.Value] = oldDynamicCapture;
        Refused(Read(world, replacementPose.Position + Start, Delta, filter: QueryFilter.DynamicsOnly));
        dynamicCaptures[body.Value] = currentDynamicCapture;
        HitContains(Read(world, replacementPose.Position + Start, Delta, filter: QueryFilter.DynamicsOnly), 1.625, 1.625);
        world.RemoveDynamic(body);

        using (var excluded = world.CreateQueryViewExcludingStatics([handle]))
        using (var included = world.CreateQueryViewExcludingStatics([]))
        {
            IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)excluded).AcquireQueryReadLease();
            var dimensions = new CapsuleQueryDimensions(0.25f, 1);
            Pose query = Pose.At(Start - world.Origin);
            Clear(((IPhysicsCapsuleGeometryQueries)excluded).SweepCapsuleCertified(dimensions, query, Delta), 8);
            HitContains(((IPhysicsCapsuleGeometryQueries)included).SweepCapsuleCertified(dimensions, query, Delta), 1.625, 1.625);
            using var foreign = new BepuPhysicsWorld(Vector3.Zero);
            Assert.Same(world, lease.SourceWorld);
            Assert.NotSame(foreign, lease.SourceWorld);
            var wrongThread = BoundedThread.Start(() => Assert.Throws<InvalidOperationException>(() => lease.AssertCurrent()));
            wrongThread.Join();
            lease.Dispose();
            Assert.Throws<ObjectDisposedException>(() => lease.AssertCurrent());
            HitContains(((IPhysicsCapsuleGeometryQueries)included).SweepCapsuleCertified(dimensions, query, Delta), 1.625, 1.625);
        }
        var disposedView = world.CreateQueryViewExcludingStatics([]);
        disposedView.Dispose();
        Assert.Throws<ObjectDisposedException>(() => ((IPhysicsCapsuleGeometryQueries)disposedView)
            .SweepCapsuleCertified(new(0.25f, 1), Pose.At(Start - world.Origin), Delta));
        using var barrier = new Barrier(2);
        var results = new CapsuleSweepResult[2];
        BoundedThread Reader(int index) => BoundedThread.Start(() =>
        {
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            BoundedThread.Arrive(barrier);
            results[index] = world.SweepCapsuleCertified(new CapsuleQueryDimensions(0.25f, 1), Pose.At(Start - world.Origin), Delta);
            BoundedThread.Arrive(barrier);
            lease.AssertCurrent();
        });
        var a = Reader(0); var b = Reader(1);
        a.Join(); b.Join();
        HitContains(results[0], 1.625, 1.625);
        SameBits(results[0], results[1]);
        QueryScratch[] readers = ConcurrentReadGateTests.Gate(world).ScratchPool.Constructed();
        Assert.True(readers.Length >= 2);
        Assert.NotSame(readers[0].GeneralSweeps.Idle, readers[1].GeneralSweeps.Idle);
        world.RemoveStatic(handle);
        Clear(Read(world, Start - world.Origin, Delta), 8);
    }

    [Fact]
    public void CompoundWarmDimensionAndRefusalQueriesAllocateNothing()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        var handle = world.AddStatic(new CompoundShape(Pair()), Pose.Identity);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        foreach ((float radius, float length) in new[] { (0.4f, 0.59999996f), (0.40000007f, 0.6000001f), (0.25f, 1f) })
        {
            var shape = new CapsuleShape(radius, length);
            var dimensions = new CapsuleQueryDimensions(radius, length);
            Pose pose = Pose.At(Start);
            var expected = world.SweepCapsuleCertified(shape, pose, Delta);
            HitContains(expected, 2d - 0.125 - radius, 2d - 0.125 - radius);
            SameBits(expected, world.SweepCapsuleCertified(dimensions, pose, Delta));
            QueryScratch retained = ConcurrentReadGateTests.Gate(world).ScratchPool.Constructed()[0];
            var scratch = retained.GeneralSweeps.Idle;
            int capacity = retained.SweepCandidates.Idle!.Capacity;
            CapsuleSweepResult a = default, b = default;
            long bytes = AllocationMeasurement.Measure(nameof(CompoundWarmDimensionAndRefusalQueriesAllocateNothing), () =>
            {
                a = world.SweepCapsuleCertified(shape, pose, Delta);
                b = world.SweepCapsuleCertified(dimensions, pose, Delta);
            });
            Assert.Equal(0, bytes);
            SameBits(expected, a); SameBits(expected, b);
            Assert.Same(scratch, retained.GeneralSweeps.Idle);
            Assert.Equal(capacity, retained.SweepCandidates.Idle!.Capacity);
        }
        // Corrupt only the second supported child's proof after an all-supported Hit(0).
        Pose overlap = Pose.Identity;
        var queryDimensions = new CapsuleQueryDimensions(0.25f, 1);
        HitContains(world.SweepCapsuleCertified(queryDimensions, overlap, Vector3.UnitX), 0, 0);
        var geometry = Geometry(world, handle);
        var childrenProperty = typeof(InstalledSweepGeometry).GetProperty("Children", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(childrenProperty);
        var children = (InstalledSweepGeometry[])childrenProperty.GetValue(geometry)!;
        var mapField = typeof(InstalledSweepGeometry).GetField("_operator", BindingFlags.NonPublic | BindingFlags.Instance)!;
        object saved = mapField.GetValue(children[1])!;
        object invalid = mapField.GetValue(children[1])!;
        typeof(InstalledPoseOperator).GetField("<X>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(invalid, new FeaturePoint(FeatureNumber.Exact(double.Epsilon), FeatureNumber.Exact(0), FeatureNumber.Exact(0)));
        mapField.SetValue(children[1], invalid);
        Refused(world.SweepCapsuleCertified(queryDimensions, overlap, Vector3.UnitX));
        CapsuleSweepResult refusal = default;
        long refusalBytes = AllocationMeasurement.Measure(nameof(CompoundWarmDimensionAndRefusalQueriesAllocateNothing) + "_late", () =>
            refusal = world.SweepCapsuleCertified(queryDimensions, overlap, Vector3.UnitX));
        Assert.Equal(0, refusalBytes);
        Refused(refusal);
        mapField.SetValue(children[1], saved);
        HitContains(world.SweepCapsuleCertified(queryDimensions, overlap, Vector3.UnitX), 0, 0);
        lease.AssertCurrent();
    }
}
