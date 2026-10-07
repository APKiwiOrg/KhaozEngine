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

// Exact point classification only. A sweep still has to bind the point to its path and prove its prefix.
public class CapsuleBoxPointWitnessTests
{
    static readonly BoxShape BoxShape = new(new Vector3(0.5f));
    delegate GeometrySign ClassifyCall(Shapes shapes, TypedIndex shape, RigidPose pose,
        ReadOnlySpan<double> centre, float radius, float halfCylinderLength);

    [Fact]
    public void ASeparatedCapsuleHasPositiveSign() =>
        Expect(BoxShape, Pose.Identity, [2, 0, 0], 0.25f, 0.5f, GeometrySign.Positive);

    [Fact]
    public void AnOverlappingCapsuleHasNegativeSign() =>
        Expect(BoxShape, Pose.Identity, [0.625, 0, 0], 0.25f, 0.5f, GeometrySign.Negative);

    [Fact]
    public void AClosedFaceTangencyHasExactlyZeroSign() =>
        Expect(BoxShape, Pose.Identity, [0.75, 0, 0], 0.25f, 0.5f, GeometrySign.Zero);

    [Fact]
    public void AClosedCornerTangencyUsesTheExactPythagoreanRelation() =>
        Expect(BoxShape, Pose.Identity, [0.875, 0, 1], 0.625f, 0.5f, GeometrySign.Zero);

    [Fact]
    public void TheCapsuleAxisExtendsTheFiniteBoxDistanceAlongY() =>
        Expect(BoxShape, Pose.Identity, [0, 1.125, 0], 0.25f, 0.5f, GeometrySign.Negative);

    [Fact]
    public void AZeroLengthAxisUsesTheSphereDistance() =>
        Expect(BoxShape, Pose.Identity, [0, 0.875, 0], 0.25f, 0f, GeometrySign.Positive);

    [Fact]
    public void ExactTranslationRetainsTangency() =>
        Expect(BoxShape, Pose.At(new Vector3(54f, 1.5f, -98f)), [54.75, 1.5, -98],
            0.25f, 0.5f, GeometrySign.Zero);

    [Fact]
    public void ATranslationLosingALowBitCannotBecomeAnExactCoordinate()
    {
        Assert.Equal(-1d, double.Epsilon - 1d);
        Expect(BoxShape, Pose.At(Vector3.UnitX), [double.Epsilon, 0, 0],
            0.25f, 0.5f, GeometrySign.Unresolved);
    }

    [Fact]
    public void AnExpandedExtentLosingALowBitRefuses()
    {
        const float tiny = 1f / (1L << 60);
        Assert.Equal(0.5d, 0.5d + tiny);
        Expect(new BoxShape(new Vector3(0.5f, tiny, 0.5f)), Pose.Identity, [0, 1, 0],
            0.25f, 0.5f, GeometrySign.Unresolved, tiny);
    }

    [Fact]
    public void UnsupportedShapePoseAndMalformedInputsRefuse()
    {
        Expect(new SphereShape(0.5f), Pose.Identity, [0, 0, 0], 0.25f, 0.5f, GeometrySign.Unresolved);
        Expect(BoxShape, new Pose(Vector3.Zero, Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)),
            [0, 0, 0], 0.25f, 0.5f, GeometrySign.Unresolved);
        Expect(BoxShape, Pose.Identity, [0, 0], 0.25f, 0.5f, GeometrySign.Unresolved);
        Expect(BoxShape, Pose.Identity, [double.NaN, 0, 0], 0.25f, 0.5f, GeometrySign.Unresolved);
        Expect(BoxShape, Pose.Identity, [0, 0, 0], -0.25f, 0.5f, GeometrySign.Unresolved);
        Expect(BoxShape, Pose.Identity, [0, 0, 0], 0.25f, -0.5f, GeometrySign.Unresolved);
    }

    static unsafe void Expect(PhysicsShape shape, Pose pose, double[] centre, float radius, float halfCylinderLength,
        GeometrySign expected, float? installedHalfHeight = null)
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
        if (installedHalfHeight.HasValue)
        {
            Assert.Equal(default(Box).TypeId, target.Shape.Type);
            simulation.Shapes[target.Shape.Type].GetShapeData(target.Shape.Index, out void* data, out int size);
            Assert.True(size >= sizeof(Box));
            Assert.Equal(installedHalfHeight.Value, ((Box*)data)->HalfHeight);
        }
        GeometrySign result = Bind()(simulation.Shapes, target.Shape, target.Pose, centre, radius, halfCylinderLength);
        lease.AssertCurrent();
        Assert.Equal(expected, result);
    }

    static ClassifyCall Bind()
    {
        Type? type = typeof(BepuPhysicsWorld).Assembly.GetType("KhaozEngine.Physics.Bepu.CapsuleBoxPointWitness");
        Assert.NotNull(type);
        MethodInfo? method = type.GetMethod("Classify", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method.CreateDelegate<ClassifyCall>();
    }
}
