using System;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using BepuSim = BepuPhysics.Simulation;

namespace KhaozEngine.Tests.Physics;

// Installed single-leaf support only. This is not a world candidate set or public certified sweep.
public class CapsuleSweepLeafProjectionTests
{
    [Fact]
    public void PositiveAxisReadsTheInstalledBoxExtent() =>
        WithProjection(new BoxShape(new Vector3(0.5f, 0.25f, 0.125f)), Pose.At(new Vector3(2f, 0f, 0f)),
            Vector3.UnitX, value => Contains(value, 2.5));

    [Fact]
    public void NegativeAxisSelectsTheOppositeFiniteFace() =>
        WithProjection(new BoxShape(new Vector3(0.5f, 0.25f, 0.125f)), Pose.At(new Vector3(2f, 0f, 0f)),
            -Vector3.UnitX, value => Contains(value, -1.5));

    [Fact]
    public void NonunitDiagonalUsesAllInstalledDimensions() =>
        WithProjection(new BoxShape(new Vector3(0.5f, 0.25f, 0.125f)), Pose.At(new Vector3(0.25f, -0.5f, 1f)),
            new Vector3(1f, 2f, -1f), value => Contains(value, -0.625));

    [Fact]
    public void TranslatedNegativeFrameCoordinatesRemainEnclosed() =>
        WithProjection(new BoxShape(new Vector3(0.5f, 0.25f, 0.125f)), Pose.At(new Vector3(256f, -128f, -256f)),
            Vector3.UnitY, value => Contains(value, -127.75));

    [Fact]
    public void AnotherRegisteredShapeCannotBeReinterpretedAsABox() =>
        WithProjection(new SphereShape(0.5f), Pose.Identity, Vector3.UnitX,
            value => Assert.False(value.IsResolved));

    [Fact]
    public void RotatedBoxCannotSilentlyUseTheIdentityFormula() =>
        WithProjection(new BoxShape(new Vector3(0.5f, 0.25f, 0.125f)),
            new Pose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)), Vector3.UnitX,
            value => Assert.False(value.IsResolved));

    [Fact]
    public void ZeroAndNonfiniteAxesCannotProduceSupport()
    {
        WithProjection(new BoxShape(Vector3.One), Pose.Identity, Vector3.Zero,
            value => Assert.False(value.IsResolved));
        WithProjection(new BoxShape(Vector3.One), Pose.Identity, new Vector3(float.NaN, 0f, 0f),
            value => Assert.False(value.IsResolved));
    }

    [Fact]
    public void InstalledSupportCanDischargeTheSingleBoxGapPremise()
    {
        WithProjection(new BoxShape(new Vector3(0.5f, 1f, 1f)), Pose.At(new Vector3(2f, 0f, 0f)),
            -Vector3.UnitX, support =>
            {
                Contains(support, -1.5);
                GeometryInterval gap = CapsuleSweptSupport.Evaluate(Vector3.Zero, 0.25f, 0.5f,
                    Vector3.UnitX, -Vector3.UnitX, support);
                Contains(gap, 0.25);
                Assert.True(gap.Lower > 0);
            });
    }

    static void Contains(GeometryInterval value, double exact)
    {
        Assert.True(value.IsResolved);
        Assert.True(value.Lower <= exact && value.Upper >= exact);
        Assert.True(double.IsFinite(value.Lower) && double.IsFinite(value.Upper));
    }

    static void WithProjection(PhysicsShape shape, Pose pose, Vector3 axis, Action<GeometryInterval> inspect)
    {
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(shape, pose);
        using IPhysicsWorldQueryView view = world.CreateQueryViewExcludingStatics([]);
        using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)view).AcquireQueryReadLease();
        lease.AssertCurrent();
        Assert.Same(world, view.SourceWorld);
        FieldInfo? field = typeof(BepuPhysicsWorld).GetField("_sim", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        BepuSim simulation = Assert.IsType<BepuSim>(field.GetValue(world));
        Assert.Equal(1, simulation.Statics.Count);
        ref var target = ref simulation.Statics[0];
        Assert.Equal(pose.Position, target.Pose.Position);
        Assert.Equal(pose.Orientation, target.Pose.Orientation);
        GeometryInterval projection = CapsuleSweepLeafProjection.Project(simulation.Shapes, target.Shape, target.Pose, axis);
        lease.AssertCurrent();
        inspect(projection);
    }

}
