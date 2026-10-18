using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using static KhaozEngine.Tests.Physics.CertifiedGeneralSweepFixture;

namespace KhaozEngine.Tests.Physics;

[Collection("AllocSensitive")]
public sealed class CertifiedHullSweepLifetimeTests
{
    [Theory]
    [InlineData("faces", false)]
    [InlineData("faces", true)]
    [InlineData("vertices", false)]
    [InlineData("vertices", true)]
    [InlineData("indices", false)]
    [InlineData("indices", true)]
    public void HullCapBoundaryWarmRefusalAllocatesNothing(string member, bool over)
    {
        CertifiedHullSweepTests.WithCapHull(member, over, (world, shape, hull) =>
        {
            using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
            var dimensions = new CapsuleQueryDimensions(0.25f, 1);
            Pose start = Pose.At(new(-10, 0, 0));
            Vector3 delta = new(20, 0, 0);
            Refused(world.SweepCapsuleCertified(dimensions, start, delta));
            Refused(world.SweepCapsuleCertified(dimensions, start, delta));
            CapsuleSweepResult measured = default;
            long bytes = AllocationMeasurement.Measure(nameof(HullCapBoundaryWarmRefusalAllocatesNothing), () =>
                measured = world.SweepCapsuleCertified(dimensions, start, delta));
            Refused(measured);
            Assert.Equal(0, bytes);
            lease.AssertCurrent();
        });
    }

    [Fact]
    public void HullMutationDimensionAndExactFallbackAllocateNothing()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle handle = world.AddStatic(CertifiedHullSweepTests.Tetrahedron(), Pose.Identity);
        Vector3 start = new(-2, 0.75f, 0.25f), delta = new(4, 0, 0);
        HitContains(Read(world, start, delta), 1.75, 1.75);
        world.Rebase(new(16, 8, -16));
        HitContains(Read(world, start - world.Origin, delta), 1.75, 1.75);
        world.Rebase(world.Origin);
        world.RemoveStatic(handle);
        Clear(Read(world, start - world.Origin, delta), 4);
        StaticHandle replacement = world.AddStatic(CertifiedHullSweepTests.Tetrahedron(), Pose.At(-world.Origin));
        HitContains(Read(world, start - world.Origin, delta), 1.75, 1.75);
        DynamicBodyHandle body = world.AddDynamic(CertifiedHullSweepTests.Tetrahedron(), Pose.At(new(40, 0, 0)), new DynamicBodyDescription(1));
        var dynamics = (Dictionary<int, (BepuPhysics.BodyHandle Handle, BepuPhysics.Collidables.TypedIndex Shape)>)Field(world, "_dynamics");
        Mutate(world, sim =>
        {
            var reference = sim.Bodies[dynamics[body.Value].Handle];
            reference.Awake = false;
        });
        Assert.False(world.IsAwake(body));
        HitContains(Read(world, new(38, 0.75f, 0.25f), delta, filter: QueryFilter.DynamicsOnly), 1.75, 1.75);
        world.SetDynamicVelocity(body, Vector3.UnitX, Vector3.Zero);
        Assert.True(world.IsAwake(body));
        world.Step(1f / 30);
        Pose pose = world.GetDynamicPose(body);
        HitContains(Read(world, pose.Position + start, delta, filter: QueryFilter.DynamicsOnly), 1.75, 1.75);
        world.RemoveDynamic(body);
        using IPhysicsQueryLease lease = world.AcquireQueryReadLease();
        QueryScratch retained = ConcurrentReadGateTests.Gate(world).ScratchPool.Constructed()[0];
        foreach ((float radius, float length) in new[] { (0.4f, 0.59999996f), (0.40000007f, 0.6000001f), (0.25f, 0f) })
        {
            var dimensions = new CapsuleQueryDimensions(radius, length);
            var shape = new CapsuleShape(radius, length);
            Pose query = Pose.At(start - world.Origin);
            Pose tangent = Pose.At(new Vector3(-radius, 0.5f, 0.5f) - world.Origin);
            CapsuleSweepResult expected = world.SweepCapsuleCertified(shape, query, delta);
            HitContains(expected, 2d - radius, 2d - radius);
            SameBits(expected, world.SweepCapsuleCertified(dimensions, query, delta));
            CapsuleSweepResult boundary = world.SweepCapsuleCertified(dimensions, tangent, Vector3.Zero);
            // These represented frame differences round toward the face or remain exact.
            Assert.InRange(Math.Abs((double)tangent.Position.X + world.Origin.X), 0, radius);
            HitContains(boundary, 0, 0);
            CapsuleSweepScratch scratch = retained.GeneralSweeps.Idle!;
            int capacity = retained.SweepCandidates.Idle!.Capacity;
            CapsuleSweepResult measuredShape = default, measuredValue = default, measuredBoundary = default;
            long bytes = AllocationMeasurement.Measure(nameof(HullMutationDimensionAndExactFallbackAllocateNothing), () =>
            {
                measuredShape = world.SweepCapsuleCertified(shape, query, delta);
                measuredValue = world.SweepCapsuleCertified(dimensions, query, delta);
                measuredBoundary = world.SweepCapsuleCertified(dimensions, tangent, Vector3.Zero);
            });
            Assert.Equal(0, bytes);
            SameBits(expected, measuredShape);
            SameBits(expected, measuredValue);
            SameBits(boundary, measuredBoundary);
            Assert.Same(scratch, retained.GeneralSweeps.Idle);
            Assert.Equal(capacity, retained.SweepCandidates.Idle!.Capacity);
        }
        var captures = (Dictionary<int, InstalledSweepGeometry?>)Field(world, "_installedSweepStatics");
        InstalledSweepGeometry geometry = captures[replacement.Value]!;
        FieldInfo field = typeof(InstalledSweepGeometry).GetField("_operator", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object saved = field.GetValue(geometry)!;
        object uncertain = field.GetValue(geometry)!;
        typeof(InstalledPoseOperator).GetField("<X>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(uncertain, new FeaturePoint(FeatureNumber.Enclosed(GeometryInterval.Enclose(-1, 1)), FeatureNumber.Exact(0), FeatureNumber.Exact(0)));
        field.SetValue(geometry, uncertain);
        var refusedDimensions = new CapsuleQueryDimensions(0.25f, 0);
        Pose refusedPose = Pose.At(start - world.Origin);
        Refused(world.SweepCapsuleCertified(refusedDimensions, refusedPose, delta));
        CapsuleSweepResult measuredRefusal = default;
        long refusedBytes = AllocationMeasurement.Measure(nameof(HullMutationDimensionAndExactFallbackAllocateNothing) + "_refusal", () =>
            measuredRefusal = world.SweepCapsuleCertified(refusedDimensions, refusedPose, delta));
        Assert.Equal(0, refusedBytes);
        Refused(measuredRefusal);
        field.SetValue(geometry, saved);
        HitContains(world.SweepCapsuleCertified(refusedDimensions, refusedPose, delta), 1.75, 1.75);
        lease.AssertCurrent();
    }
}
