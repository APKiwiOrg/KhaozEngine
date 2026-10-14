using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public sealed class CapsuleGeometryQueryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValueQueriesPreserveExactShapeResultsAndSelectedExclusions(bool exclude)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        StaticHandle nearest = world.AddStatic(new BoxShape(new(0.5f)), Pose.Identity);
        world.AddStatic(new BoxShape(new(0.5f)), Pose.At(new(2, 0, 0)));
        using var view = world.CreateQueryViewExcludingStatics(exclude ? [nearest] : []);
        IPhysicsCapsuleGeometryQueries queries = Assert.IsAssignableFrom<IPhysicsCapsuleGeometryQueries>(view);
        using var lease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        var shape = new CapsuleShape(MathF.BitIncrement(0.25f), MathF.BitIncrement(1f));
        var dimensions = new CapsuleQueryDimensions(shape.Radius, shape.Length);
        Pose start = Pose.At(new(-2, 0, 0));
        Vector3 delta = new(10, 0, 0);
        CapsuleSweepResult reference = queries.SweepCapsuleCertified(shape, start, delta, QueryFilter.StaticsOnly);
        CapsuleSweepResult value = queries.SweepCapsuleCertified(dimensions, start, delta, QueryFilter.StaticsOnly);
        Assert.Equal(CapsuleSweepStatus.Hit, value.Status);
        Assert.Equal(reference, value);
        Assert.True(value.ClearThroughDistance > (exclude ? 3 : 1));

        Span<CapsuleContact> referenceContacts = stackalloc CapsuleContact[8], valueContacts = stackalloc CapsuleContact[8];
        Pose contactPose = Pose.At(new(0.6f, 0, 0));
        CapsuleContactResult shapeContacts = queries.QueryCapsuleContacts(shape, contactPose, 0,
            referenceContacts, QueryFilter.StaticsOnly);
        CapsuleContactResult dimensionContacts = queries.QueryCapsuleContacts(dimensions, contactPose, 0,
            valueContacts, QueryFilter.StaticsOnly);
        Assert.True(dimensionContacts.Complete);
        Assert.Equal(shapeContacts, dimensionContacts);
        Assert.Equal(referenceContacts[..shapeContacts.Written].ToArray(), valueContacts[..dimensionContacts.Written].ToArray());
        Assert.Equal(exclude, dimensionContacts.Written == 0);
    }

    [Fact]
    public void ValueQueriesRetainValidationAndDisposedViewGuards()
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        using var view = world.CreateQueryViewExcludingStatics([]);
        var queries = (IPhysicsCapsuleGeometryQueries)view;
        var invalid = new CapsuleQueryDimensions(float.NaN, 1);
        var valid = new CapsuleQueryDimensions(0.25f, 1);
        Assert.Equal(default, queries.SweepCapsuleCertified(invalid, Pose.Identity, Vector3.UnitX));
        Assert.Throws<ArgumentOutOfRangeException>(() => queries.QueryCapsuleContacts(invalid, Pose.Identity, 0, []));
        using (var lease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease())
            Assert.Throws<InvalidOperationException>(view.Dispose);
        view.Dispose();
        Assert.Throws<ObjectDisposedException>(() => queries.SweepCapsuleCertified(valid, Pose.Identity, Vector3.UnitX));
        Assert.Throws<ObjectDisposedException>(() => queries.QueryCapsuleContacts(valid, Pose.Identity, 0, []));
    }
}
