using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using BepuSim = BepuPhysics.Simulation;

namespace KhaozEngine.Tests.Physics;

// The first integrated backend domain is identity-oriented boxes. Other selected bound families refuse.
public partial class BepuCertifiedCapsuleSweepTests
{
    static readonly BoxShape Box = new(new Vector3(0.5f));
    static readonly CapsuleShape Capsule = new(0.25f, 1f);

    [Fact]
    public void EmptyOwnerCertifiesTheWholeRequest()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Clear(Read(world), 4);
    }

    [Fact]
    public void EmptySelectedViewUsesItsRealOwnerLease()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        Clear(Read(view), 4);
    }

    [Fact]
    public void StaticBoxEntryHasAContainingMetreBracket()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.Identity);
        Contains(Read(world), 1.25);
    }

    [Fact]
    public void ReversedInsertionOrderKeepsTheAggregateResult()
    {
        using var a = new BepuPhysicsWorld(Vector3.Zero);
        using var b = new BepuPhysicsWorld(Vector3.Zero);
        a.AddStatic(Box, Pose.Identity);
        a.AddStatic(Box, Pose.At(new(2, 0, 0)));
        b.AddStatic(Box, Pose.At(new(2, 0, 0)));
        b.AddStatic(Box, Pose.Identity);
        CapsuleSweepResult first = Read(a, delta: new(10, 0, 0));
        Contains(first, 1.25);
        Assert.Equal(first, Read(b, delta: new(10, 0, 0)));
    }

    [Fact]
    public void ExcludingTheNearestBoxLeavesTheNextRealHit()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle nearest = world.AddStatic(Box, Pose.Identity);
        world.AddStatic(Box, Pose.At(new(2, 0, 0)));
        using var view = world.CreateQueryViewExcludingStatics([nearest]);
        Contains(Read(view, delta: new(10, 0, 0)), 3.25);
    }

    [Fact]
    public void ThinInteriorColliderIsNotLostBetweenClearEndpoints()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new(0.03125f, 0.5f, 0.5f)), Pose.Identity);
        Contains(Read(world), 1.71875);
    }

    [Fact]
    public void CeilingHitIncludesTheFullCapsuleHeight()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.Identity);
        Contains(Read(world, pose: Pose.At(new(0, -2, 0)), delta: new(0, 4, 0)), 0.75);
    }

    [Fact]
    public void ASeparatedBedCannotMaskTheSideWall()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new BoxShape(new(10, 0.25f, 10)), Pose.At(new(0, -1, 0)));
        world.AddStatic(Box, Pose.Identity);
        Contains(Read(world, pose: Pose.At(new(-2, 0.001f, 0))), 1.25);
    }

    [Fact]
    public void InitialOverlapHasNoForwardClearPrefix()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.Identity);
        CapsuleSweepResult result = Read(world, pose: Pose.Identity);
        Contains(result, 0);
        Assert.Equal(0f, result.ImpactDistance);
    }

    [Fact]
    public void InitialTangencyMovingAwayIsStillAClosedHit()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.Identity);
        CapsuleSweepResult result = Read(world, pose: Pose.At(new(-0.75f, 0, 0)), delta: -Vector3.UnitX);
        Contains(result, 0);
        Assert.Equal(0f, result.ImpactDistance);
    }

    [Fact]
    public void ZeroDisplacementDistinguishesClearFromContact()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.Identity);
        Clear(Read(world, delta: Vector3.Zero), 0);
        Contains(Read(world, pose: Pose.Identity, delta: Vector3.Zero), 0);
    }

    [Fact]
    public void UnencodableEndpointRemainsAComposedRefusal()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, Pose.Identity);
        Refused(Read(world, pose: Pose.At(new(-1.75f, -2, 0)), delta: new(1, 2, 0)));
    }

    [Fact]
    public void UnsupportedSelectedBoundsRefuseEvenOutsideTheRawAperture()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(new SphereShape(0.5f), Pose.At(new(100, 0, 0)));
        Refused(Read(world));
    }

    [Fact]
    public void RotatedSelectedBoxesDoNotBorrowAnIdentityCertificate()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(Box, new Pose(new(100, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)));
        Refused(Read(world));
    }

    [Fact]
    public void UnsupportedQueryOrientationIsNotTreatedAsUpright()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Refused(Read(world, pose: new Pose(new(-2, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitX, 0.5f))));
    }

    [Fact]
    public void InvalidQueriesDoNotInvalidateOtherwiseCurrentEvidence()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        Refused(Read(world, delta: new(float.NaN, 0, 0)));
        Refused(Read(world, capsule: new CapsuleShape(0, 1)));
        Refused(Read(world, filter: new QueryFilter(QueryMobility.All, 1)));
        Refused(Read(world, filter: new QueryFilter((QueryMobility)99)));
        Clear(Read(world), 4);
    }

    static CapsuleSweepResult Read(object receiver, Pose? pose = null, Vector3? delta = null,
        CapsuleShape? capsule = null, QueryFilter filter = default)
    {
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)receiver).AcquireQueryReadLease();
        lease.AssertCurrent();
        Assert.Same(receiver is IPhysicsWorldQueryView view ? view.SourceWorld : receiver, lease.SourceWorld);
        var sweep = Assert.IsAssignableFrom<IPhysicsCapsuleSweep>(receiver);
        CapsuleSweepResult result = sweep.SweepCapsuleCertified(capsule ?? Capsule,
            pose ?? Pose.At(new(-2, 0, 0)), delta ?? new Vector3(4, 0, 0), filter);
        lease.AssertCurrent();
        return result;
    }

    static void Clear(CapsuleSweepResult result, float encodedLength)
    {
        Assert.Equal(CapsuleSweepStatus.Clear, result.Status);
        Assert.Equal(encodedLength, result.ClearThroughDistance);
        Assert.Null(result.ImpactDistance);
        Assert.InRange(result.CertifiedErrorMetres, 0, 0.001f);
    }

    static void Contains(CapsuleSweepResult result, double distance)
    {
        Assert.Equal(CapsuleSweepStatus.Hit, result.Status);
        Assert.NotNull(result.ImpactDistance);
        Assert.True(result.ClearThroughDistance <= distance && result.ImpactDistance.Value >= distance);
        Assert.InRange((double)result.ImpactDistance.Value - result.ClearThroughDistance, 0, result.CertifiedErrorMetres);
        Assert.InRange(result.CertifiedErrorMetres, 0, 0.001f);
    }

    static void Refused(CapsuleSweepResult result) => Assert.Equal(default, result);

    static BepuSim Simulation(BepuPhysicsWorld world)
    {
        FieldInfo? field = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<BepuSim>(field.GetValue(world));
    }
}
