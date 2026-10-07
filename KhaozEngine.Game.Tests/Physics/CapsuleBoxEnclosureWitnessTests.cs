using System;
using System.Numerics;
using System.Reflection;
using BepuPhysics;
using BepuPhysics.Collidables;
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;
using Xunit;
using BepuSim = BepuPhysics.Simulation;

namespace KhaozEngine.Tests.Physics;

public class CapsuleBoxEnclosureWitnessTests
{
    static readonly BoxShape BoxShape = new(new Vector3(0.5f));

    [Fact]
    public void AWhollyOverlappingEnclosureHasNegativeSign() =>
        Expect(V(0.625, 0.626, -0.01, 0.01, -0.01, 0.01), GeometrySign.Negative);

    [Fact]
    public void AWhollySeparatedEnclosureHasPositiveSign() =>
        Expect(V(0.9, 1, -0.01, 0.01, -0.01, 0.01), GeometrySign.Positive);

    [Fact]
    public void AContactStraddlingEnclosureCannotUseItsTangentMidpoint() =>
        Expect(V(0.74, 0.76, 0, 0, 0, 0), GeometrySign.Unresolved);

    [Fact]
    public void ExactNormalDistanceCanProveTangencyWithUncertainInteriorAxes() =>
        Expect(V(0.75, 0.75, -0.1, 0.1, -0.1, 0.1), GeometrySign.Zero);

    [Fact]
    public void TranslatedTangencyStillKeepsInteriorUncertainty()
    {
        Expect(V(54.75, 54.75, 1.4, 1.6, -98.1, -97.9), GeometrySign.Zero,
            Pose.At(new Vector3(54f, 1.5f, -98f)));
    }

    [Fact]
    public void AnUncertainNonzeroDistanceContributionCannotBecomeExact() =>
        Expect(V(0.625, 0.625, 1.2, 1.3, 0, 0), GeometrySign.Unresolved);

    [Fact]
    public void AnUnresolvedCoordinateRefusesTheWholeWitness() =>
        Expect(new GeometryVector(GeometryInterval.Exact(0.75), default, GeometryInterval.Exact(0)),
            GeometrySign.Unresolved);

    [Fact]
    public void AProvedPathEnclosureCanEstablishOverlapAtItsFraction()
    {
        GeometryVector point = CapsuleSweepPath.Point(Vector3.Zero, new Vector3(4f, 1.1f, 0f), 1d / 3d);
        Assert.True(point.IsResolved && point.Y.Lower < point.Y.Upper);
        Expect(point, GeometrySign.Negative, Pose.At(new Vector3(1.5f, 0f, 0f)));
    }

    [Fact]
    public void LossyTranslationCanStillProveSeparationWithoutBecomingExact() =>
        Expect(V(double.Epsilon, double.Epsilon, 0, 0, 0, 0), GeometrySign.Positive, Pose.At(Vector3.UnitX));

    [Fact]
    public void UnsupportedGeometryAndInvalidRadiusRemainUnresolved()
    {
        Expect(V(0, 0, 0, 0, 0, 0), GeometrySign.Unresolved, shape: new SphereShape(0.5f));
        Expect(V(0, 0, 0, 0, 0, 0), GeometrySign.Unresolved,
            new Pose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)));
        Expect(V(0, 0, 0, 0, 0, 0), GeometrySign.Unresolved, radius: float.NaN);
    }

    static GeometryVector V(double xl, double xu, double yl, double yu, double zl, double zu) =>
        new(GeometryInterval.Enclose(xl, xu), GeometryInterval.Enclose(yl, yu), GeometryInterval.Enclose(zl, zu));

    static void Expect(GeometryVector centre, GeometrySign expected, Pose? pose = null,
        PhysicsShape? shape = null, float radius = 0.25f)
    {
        Pose sourcePose = pose ?? Pose.Identity;
        using var world = new BepuPhysicsWorld(Vector3.Zero);
        world.AddStatic(shape ?? BoxShape, sourcePose);
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
        GeometrySign result = CapsuleBoxPointWitness.ClassifyEnclosure(
            simulation.Shapes, target.Shape, target.Pose, centre, radius, 0.5f);
        lease.AssertCurrent();
        Assert.Equal(expected, result);
    }
}
