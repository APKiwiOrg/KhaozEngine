using System;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public partial class SupportFootprintTests
{
    [Fact]
    public void CurvedWorkContactBandSnapCannotReturnCachedSuccess()
    {
        var leaf = RepresentedLeaf(SupportCurvedShape.Cylinder,
            new(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6)), 0.25, 0.5);
        var work = new SupportCurvedWork(85);
        work.Begin();
        var geometry = new SupportTiltedCurvedGeometry(work);
        Assert.True(geometry.Begin(leaf, 0, 0, 0.2));

        GeometrySign result = geometry.Contact(SupportCurvedShape.Cylinder, 0, 0.2,
            0.5551881949939084, 2, out var lowest);
        var refused = work.Snapshot;
        Assert.Equal(SupportCurvedWorkFailure.WorkLimit, refused.Failure);
        Assert.Equal(85, refused.Consumed);
        work.Fail(SupportCurvedWorkFailure.RootStorage);
        Assert.False(work.TryCharge(SupportCurvedWorkKind.Sign));
        Assert.Equal(refused, work.Snapshot);
        Assert.Equal(GeometrySign.Unresolved, result);
        Assert.False(lowest.IsResolved);
        Assert.Equal(GeometrySign.Unresolved,
            geometry.Contact(SupportCurvedShape.Cylinder, 0, 0.2, -2, 2, out lowest));
        Assert.False(lowest.IsResolved);
        Assert.Equal(refused, work.Snapshot);
        Assert.False(geometry.CapUpward(2).IsResolved);
        Assert.False(geometry.Surface(2, GeometryInterval.Exact(0), GeometryInterval.Exact(0)).IsResolved);
        var function = (ISupportCurvedRootFunction)geometry;
        Assert.False(function.Evaluate(GeometryInterval.Exact(0), false).IsResolved);
        Assert.Equal(GeometrySign.Unresolved, function.Sign(2));
        Assert.Equal(refused, work.Snapshot);
    }

    [Fact]
    public void CurvedWorkCapRefinementCannotRetainCachedWitness()
    {
        var leaf = RepresentedLeaf(SupportCurvedShape.Cylinder,
            new(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 6)), 0.25, 0.5);
        var work = new SupportCurvedWork(85);
        work.Begin();
        var geometry = new SupportTiltedCurvedGeometry(work);
        Assert.True(geometry.Begin(leaf, 0, 0, 0.2));
        Assert.Equal(GeometrySign.Positive,
            geometry.Contact(SupportCurvedShape.Cylinder, 0, 0.2, -2, 2, out _));
        Assert.Equal(85, work.Snapshot.Consumed);
        Assert.Equal(SupportCurvedWorkFailure.None, work.Snapshot.Failure);
        var witness = new SupportTiltedCurvedGeometry.Witness(
            geometry.Surface(2, GeometryInterval.Exact(0), GeometryInterval.Exact(0)), leaf.Axis);
        Assert.True(witness.Position.IsResolved);
        Assert.True(witness.Normal.IsResolved);

        geometry.RefineCapUpward(SupportNeighborhoodCurved.TopCap, 0.5, ref witness);
        var refused = work.Snapshot;
        Assert.Equal(SupportCurvedWorkFailure.WorkLimit, refused.Failure);
        Assert.Equal(85, refused.Consumed);
        work.Fail(SupportCurvedWorkFailure.AdmissionStorage);
        Assert.False(work.TryCharge(SupportCurvedWorkKind.Sign));
        Assert.Equal(refused, work.Snapshot);
        Assert.False(witness.Position.IsResolved);
        Assert.False(witness.Normal.IsResolved);
        geometry.RefineCapUpward(SupportNeighborhoodCurved.TopCap, 0.5, ref witness);
        Assert.False(witness.Position.IsResolved);
        Assert.Equal(refused, work.Snapshot);
    }
}
