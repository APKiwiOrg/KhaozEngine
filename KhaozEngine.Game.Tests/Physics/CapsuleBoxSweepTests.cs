using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using BepuSim = BepuPhysics.Simulation;

namespace KhaozEngine.Tests.Physics;

public class CapsuleBoxSweepTests
{

    [Fact]
    public void ClearCertifiesTheOriginalVectorWithItsRequiredLengthEncoding()
    {
        CapsuleSweepResult result = Query(Vector3.Zero, new(1, 2, 0), Pose.At(new(54, 0, 0)));
        Assert.Equal(CapsuleSweepStatus.Clear, result.Status);
        Assert.Equal((float)Math.Sqrt(5), result.ClearThroughDistance);
        Assert.Null(result.ImpactDistance);
        Assert.Equal(0f, result.CertifiedErrorMetres);
    }

    [Fact]
    public void ActualBoxEntryReturnsABoundedMetreInterval() =>
        Contains(Query(new(-2, 0, 0), new(4, 0, 0)), 1.25);

    [Fact]
    public void ReversedTranslatedEntryKeepsTheSameMetreDistance() =>
        Contains(Query(new(56, 1.5f, -98), new(-4, 0, 0), Pose.At(new(54, 1.5f, -98))), 1.25);

    [Fact]
    public void AThinInteriorBoxCannotBecomeClearBecauseBothEndpointsAreClear() =>
        Contains(Query(new(-2, 0, 0), new(4, 0, 0),
            shape: new BoxShape(new(0.03125f, 0.5f, 0.5f))), 1.71875);

    [Fact]
    public void RepresentableEndpointIncludesItsClosedTangency() =>
        Contains(Query(new(-4.75f, 0, 0), new(4, 0, 0)), 4);

    [Fact]
    public void UnencodableEndpointProducesAComposedRefusal()
    {
        // X is less than -.75 until t=1, where Y=0 is inside the expanded box's side face.
        // The first event is therefore exactly at sqrt(5), which has no exact binary32 encoding.
        Refused(Query(new(-1.75f, -2, 0), new(1, 2, 0)));
    }

    [Fact]
    public void StationaryClearAndInitialContactRemainDifferent()
    {
        CapsuleSweepResult clear = Query(new(2, 0, 0), Vector3.Zero);
        Assert.Equal(CapsuleSweepStatus.Clear, clear.Status);
        Assert.Equal(0f, clear.ClearThroughDistance);
        Assert.Null(clear.ImpactDistance);
        CapsuleSweepResult contact = Query(new(-0.75f, 0, 0), Vector3.Zero);
        Contains(contact, 0);
        Assert.Equal(0f, contact.ImpactDistance);
    }

    [Fact]
    public void UnsupportedShapeAndPoseNeverUseTheLegacySweep()
    {
        Refused(Query(new(-2, 3, 0), new(4, 0, 0), shape: new SphereShape(0.5f)));
        Refused(Query(new(-2, 3, 0), new(4, 0, 0),
            new Pose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f))));
    }

    [Fact]
    public void WorkExhaustionDoesNotPublishAnIntermediatePrefix() =>
        Refused(Query(new(-2, 0, 0), new(4, 0, 0), maximumCells: 1));

    [Fact]
    public void UnachievableAndInvalidErrorBudgetsRefuse()
    {
        Refused(Query(new(-2, 0, 0), new(4, 0, 0), error: float.Epsilon));
        Refused(Query(new(-2, 3, 0), new(4, 0, 0), error: float.NaN));
        Refused(Query(new(-2, 3, 0), new(4, 0, 0), error: -float.Epsilon));
    }

    static void Contains(CapsuleSweepResult result, double expected)
    {
        Assert.True(result.IsValid);
        Assert.Equal(CapsuleSweepStatus.Hit, result.Status);
        Assert.NotNull(result.ImpactDistance);
        Assert.True(result.ClearThroughDistance <= expected && result.ImpactDistance.Value >= expected);
        Assert.InRange((double)result.ImpactDistance.Value - result.ClearThroughDistance,
            0d, result.CertifiedErrorMetres);
        Assert.InRange(result.CertifiedErrorMetres, 0f, 0.001f);
    }

    static void Refused(CapsuleSweepResult result) => Assert.Equal(default, result);

    static CapsuleSweepResult Query(Vector3 centre, Vector3 delta, Pose? pose = null,
        PhysicsShape? shape = null, float error = 0.001f, int maximumCells = 256)
    {
        Pose sourcePose = pose ?? Pose.Identity;
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(shape ?? new BoxShape(new Vector3(0.5f)), sourcePose);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        lease.AssertCurrent();
        Assert.Same(world, view.SourceWorld);
        FieldInfo? field = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        BepuSim simulation = Assert.IsType<BepuSim>(field.GetValue(world));
        Assert.Equal(1, simulation.Statics.Count);
        ref var target = ref simulation.Statics[0];
        Assert.Equal(sourcePose.Position, target.Pose.Position);
        Assert.Equal(sourcePose.Orientation, target.Pose.Orientation);
        CapsuleSweepResult result = CapsuleBoxSweep.Sweep(simulation.Shapes, target.Shape,
            target.Pose, centre, 0.25f, 0.5f, delta, error, maximumCells);
        lease.AssertCurrent();
        return result;
    }
}
