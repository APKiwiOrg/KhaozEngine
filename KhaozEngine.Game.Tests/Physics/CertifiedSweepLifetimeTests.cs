using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class CertifiedSweepLifetimeTests
{
    static readonly Quaternion Cyclic = new(0.5f, 0.5f, 0.5f, 0.5f);
    static readonly BoxShape Box = new(new(0.5f, 0.25f, 0.125f));
    static readonly Vector3 Start = new(-2, 0, 0), Delta = new(4, 0, 0);
    static readonly CapsuleShape Capsule = new(0.25f, 1);

    [Fact]
    public void RotatedBoxMutationAndFrameEvidenceStayCurrent()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle original = world.AddStatic(Box, new Pose(Vector3.Zero, Cyclic));
        using var excluded = world.CreateQueryViewExcludingStatics([original]);
        using var included = world.CreateQueryViewExcludingStatics([]);
        using (IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)excluded).AcquireQueryReadLease())
        {
            Assert.Same(world, lease.SourceWorld);
            Clear(Query(excluded), 4);
            HitContains(Query(included), 1.625, 1.625);
            lease.AssertCurrent();
        }
        IPhysicsQueryLease disposed = world.AcquireQueryReadLease();
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.AssertCurrent());
        HitContains(Read(world, Start, Delta), 1.625, 1.625);
        world.RemoveStatic(original);
        Clear(Read(world, Start, Delta), 4);
        StaticHandle replacement = world.AddStatic(Box, new Pose(Vector3.Zero, Cyclic));
        Assert.NotEqual(original, replacement);
        using (IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)excluded).AcquireQueryReadLease())
            HitContains(Query(excluded), 1.625, 1.625);
        world.Rebase(new(16, 8, -16));
        CapsuleSweepResult translated = Read(world, Start - world.Origin, Delta);
        HitContains(translated, 1.625, 1.625);
        world.Rebase(world.Origin);
        Assert.Equal(translated, Read(world, Start - world.Origin, Delta));

        DynamicBodyHandle body = world.AddDynamic(Box, new Pose(new(40, 0, 0), Cyclic), new DynamicBodyDescription(1));
        var dynamics = (Dictionary<int, (BepuPhysics.BodyHandle Handle, BepuPhysics.Collidables.TypedIndex Shape)>)Field(world, "_dynamics");
        Mutate(world, simulation =>
        {
            var reference = simulation.Bodies[dynamics[body.Value].Handle];
            reference.Awake = false;
        });
        Assert.False(world.IsAwake(body));
        HitContains(Read(world, new(38, 0, 0), Delta, filter: QueryFilter.DynamicsOnly), 1.625, 1.625);
        world.SetDynamicVelocity(body, Vector3.UnitX, Vector3.Zero);
        Assert.True(world.IsAwake(body));
        world.Step(1f / 30);
        Pose current = world.GetDynamicPose(body);
        Vector3 start = current.Position - new Vector3(2, 0, 0);
        HitContains(Read(world, start, Delta, filter: QueryFilter.DynamicsOnly), 1.625, 1.625);
        ConstraintDescription description = ConstraintDescription.HingeJoint(ConstraintAttachment.OnBody(body),
            ConstraintAttachment.AtWorld(current.Position), Vector3.Zero, Vector3.Zero, Vector3.UnitY, Vector3.UnitY)
            with
        { Motor = ConstraintMotor.HingeVelocity, MotorTarget = 0 };
        ConstraintHandle joint = world.AddConstraint(description);
        HitContains(Read(world, Start - world.Origin, Delta, filter: QueryFilter.StaticsOnly), 1.625, 1.625);
        world.SetConstraintTarget(joint, 0.5f);
        HitContains(Read(world, Start - world.Origin, Delta, filter: QueryFilter.StaticsOnly), 1.625, 1.625);
        world.RemoveConstraint(joint);
        world.RemoveDynamic(body);
        HitContains(Read(world, Start - world.Origin, Delta), 1.625, 1.625);
    }

    [Fact]
    public void RotatedBoxStaleInstallationAndMissedMutationRefuse()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle original = world.AddStatic(Box, new Pose(Vector3.Zero, Cyclic));
        var captures = (Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics");
        InstalledSweepGeometry old = captures[original.Value]!;
        world.RemoveStatic(original);
        StaticHandle replacement = world.AddStatic(Box, new Pose(new(1, 0, 0), Cyclic));
        HitContains(Read(world, Start, Delta), 2.625, 2.625);
        InstalledSweepGeometry current = captures[replacement.Value]!;
        captures[replacement.Value] = old;
        Refused(Read(world, Start, Delta));
        captures[replacement.Value] = current;
        HitContains(Read(world, Start, Delta), 2.625, 2.625);
        ((IDisposable)Call(world, "EnterMutation", false, true)!).Dispose();
        Refused(Read(world, Start, Delta));
        world.AddStatic(Box, new Pose(new(20, 0, 0), Cyclic));
        Refused(Read(world, Start, Delta));
    }

    [Fact]
    public void RotatedBoxFailedMutationCannotBeHealedByAnUnrelatedAdd()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, new Pose(Vector3.Zero, Cyclic));
        HitContains(Read(world, Start, Delta), 1.625, 1.625);
        ConstraintDescription broken = ConstraintDescription.BallSocketJoint(ConstraintAttachment.AtWorld(Vector3.Zero),
            ConstraintAttachment.OnBody(new DynamicBodyHandle(99999)), Vector3.Zero, Vector3.Zero);
        Assert.Throws<ArgumentException>(() => world.AddConstraint(broken));
        Refused(Read(world, Start, Delta));
        world.AddStatic(Box, new Pose(new(20, 0, 0), Cyclic));
        Refused(Read(world, Start, Delta));
    }

    [Fact]
    public void RotatedBoxDimensionAndWarmQueryParity()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, new Pose(Vector3.Zero, Cyclic));
        using (IPhysicsQueryLease lease = world.AcquireQueryReadLease())
        {
            foreach ((float radius, float length) in new[] { (0.4f, 0.59999996f), (0.40000007f, 0.6000001f), (0.25f, 1f) })
            {
                var shape = new CapsuleShape(radius, length);
                var dimensions = new CapsuleQueryDimensions(radius, length);
                Pose tangent = Pose.At(new(-0.125f - radius, 0, 0));
                CapsuleSweepResult shapeResult = world.SweepCapsuleCertified(shape, Pose.At(Start), Delta);
                HitContains(shapeResult, 2d - 0.125 - radius, 2d - 0.125 - radius);
                SameBits(shapeResult, world.SweepCapsuleCertified(dimensions, Pose.At(Start), Delta));
                CapsuleSweepResult boundary = world.SweepCapsuleCertified(dimensions, tangent, Vector3.Zero);
                Assert.True(boundary.IsComplete);
                CapsuleSweepResult refused = world.SweepCapsuleCertified(dimensions, Pose.At(Start), Delta, new(QueryMobility.All, 1));
                Refused(refused);
                CapsuleSweepResult measuredShape = default, measuredValue = default, measuredBoundary = default, measuredRefusal = default;
                QueryScratch retained = ConcurrentReadGateTests.Gate(world).ScratchPool.Constructed()[0];
                CapsuleSweepScratch retainedGeneral = retained.GeneralSweeps.Idle!;
                int capacity = retained.SweepCandidates.Idle!.Capacity;
                long bytes = AllocationMeasurement.Measure(nameof(RotatedBoxDimensionAndWarmQueryParity), () =>
                {
                    measuredShape = world.SweepCapsuleCertified(shape, Pose.At(Start), Delta);
                    measuredValue = world.SweepCapsuleCertified(dimensions, Pose.At(Start), Delta);
                    measuredBoundary = world.SweepCapsuleCertified(dimensions, tangent, Vector3.Zero);
                    measuredRefusal = world.SweepCapsuleCertified(dimensions, Pose.At(Start), Delta, new(QueryMobility.All, 1));
                });
                Assert.Equal(0, bytes);
                SameBits(shapeResult, measuredShape);
                SameBits(shapeResult, measuredValue);
                SameBits(boundary, measuredBoundary);
                Refused(measuredRefusal);
                Assert.Same(retainedGeneral, retained.GeneralSweeps.Idle);
                Assert.Equal(capacity, retained.SweepCandidates.Idle!.Capacity);
            }
            QueryScratch scratch = ConcurrentReadGateTests.Gate(world).ScratchPool.Constructed()[0];
            CapsuleSweepScratch outer = scratch.GeneralSweeps.Take();
            try
            {
                HitContains(Query(world), 1.625, 1.625);
                Assert.NotSame(outer, scratch.GeneralSweeps.Idle);
            }
            finally { scratch.GeneralSweeps.Restore(outer); }
            HitContains(Query(world), 1.625, 1.625);
        }
        using var barrier = new Barrier(2);
        CapsuleSweepResult[] results = new CapsuleSweepResult[2];
        BoundedThread StartReader(int index) => BoundedThread.Start(() =>
        {
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            BoundedThread.Arrive(barrier);
            results[index] = Query(world);
            BoundedThread.Arrive(barrier);
            lease.AssertCurrent();
        });
        BoundedThread a = StartReader(0), b = StartReader(1);
        a.Join(); b.Join();
        HitContains(results[0], 1.625, 1.625);
        SameBits(results[0], results[1]);
        QueryScratch[] readers = ConcurrentReadGateTests.Gate(world).ScratchPool.Constructed();
        Assert.True(readers.Length >= 2);
        Assert.NotSame(readers[0].GeneralSweeps.Idle, readers[1].GeneralSweeps.Idle);
    }

    static CapsuleSweepResult Query(object receiver) => ((IPhysicsCapsuleSweep)receiver).SweepCapsuleCertified(Capsule, Pose.At(Start), Delta);
}
