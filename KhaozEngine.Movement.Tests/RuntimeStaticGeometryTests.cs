using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class RuntimeStaticGeometryTests
{
    public static IEnumerable<object[]> Shapes()
    {
        yield return [new SphereShape(1)];
        yield return [new BoxShape(Vector3.One)];
        yield return [new CapsuleShape(0.5f, 1)];
        yield return [new CylinderShape(1, 2)];
        yield return [new ConvexHullShape([new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1), new(0, 2, 0)])];
        yield return [new TriangleMeshShape([new(0, -1, -1), new(0, 1, -1), new(0, 0, 1)], [0, 1, 2])];
        yield return [new CompoundShape([new(new CompoundShape([new(new BoxShape(Vector3.One), Pose.Identity)]), Pose.Identity)])];
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void AllSupportedShapesAdmitRotatedAbsolutePosesAndSurviveRebase(PhysicsShape shape)
    {
        using var scene = new OrdinaryContactScene(origin: new(8, 0, -8),
            extent: new(new(-20), new(20), 0, 0));
        var request = RuntimeStaticChange.Set(new("world", "shape"), shape,
            new(new(9, 0, -8), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f)));
        scene.Registration.ApplyRuntimeStatics([request]);
        using (var known = scene.Acquire()) { }
        scene.Physics.Rebase(new(16, 0, -16));
        using var read = scene.Physics.AcquireQueryReadLease();
        Assert.Equal(MovementAvailability.Known, scene.Registration.Authenticate(read));
        Assert.True(scene.Physics.Raycast(new(-11, 0.25f, 8), Vector3.UnitX, 8, out var hit));
        Assert.Equal(MovementAvailability.Known, scene.Registration.TryBind(read, hit.Body!.Value, int.MaxValue, out var owner));
        Assert.Equal(new MovementSupportKey("world", "shape"), owner);
    }

    [Fact]
    public void SetCopiesCallerMeshHullAndNestedChildArraysBeforeAnyInstallation()
    {
        Vector3[] vertices = [new(0, -1, -1), new(0, 1, -1), new(0, 0, 1)];
        int[] indices = [0, 1, 2];
        Vector3[] points = [new(-1, 0, -1), new(1, 0, -1), new(0, 0, 1), new(0, 2, 0)];
        CompoundChild[] nested = [new(new ConvexHullShape(points), Pose.Identity)];
        CompoundChild[] children = [new(new CompoundShape(nested), Pose.Identity)];
        var mesh = RuntimeStaticChange.Set(new("world", "mesh"), new TriangleMeshShape(vertices, indices), Pose.At(new(-4, 0, 0)));
        var compound = RuntimeStaticChange.Set(new("world", "compound"), new CompoundShape(children), Pose.At(new(4, 0, 0)));
        Array.Fill(vertices, new Vector3(float.NaN));
        Array.Fill(indices, int.MaxValue);
        Array.Fill(points, new Vector3(1000));
        nested[0] = new(new BoxShape(new(100)), Pose.Identity);
        children[0] = new(new SphereShape(100), Pose.Identity);
        using var scene = new OrdinaryContactScene(extent: new(new(-20), new(20), 0, 0));
        scene.Registration.ApplyRuntimeStatics([mesh, compound]);
        Assert.True(scene.Physics.Raycast(new(-8, 0, 0), Vector3.UnitX, 6, out _));
        Assert.True(scene.Physics.Raycast(new(0, 0.5f, 0), Vector3.UnitX, 6, out _));
        Assert.False(scene.Physics.Raycast(new(10, 4, 0), -Vector3.UnitY, 5, out _));
        using var known = scene.Acquire();
    }

    [Fact]
    public void FixedExtentReservesTallGeometryAndHonorsBaseAlignedCylinder()
    {
        var cylinder = RuntimeStaticChange.Set(new("world", "cylinder"), new CylinderShape(1, 2), Pose.At(new(0, 1, 0)));
        using var tooShort = new OrdinaryContactScene(extent: new(new(-20, -4, -20), new(20, 2, 20), 0, 0));
        long before = tooShort.Physics.StaticGeometryGeneration;
        Assert.Throws<ArgumentException>(() => tooShort.Registration.ApplyRuntimeStatics([cylinder]));
        Assert.Equal(before, tooShort.Physics.StaticGeometryGeneration);
        using var reserved = new OrdinaryContactScene();
        reserved.Registration.ApplyRuntimeStatics([cylinder]);
        Assert.True(reserved.Physics.Raycast(new(0, 4, 0), -Vector3.UnitY, 5, out var hit));
        Assert.Equal(3, hit.Point.Y);
        var tall = RuntimeStaticChange.Set(new("world", "tall"), new BoxShape(new(1, 3, 1)), Pose.At(new(5, 0, 0)));
        reserved.Registration.ApplyRuntimeStatics([tall]);
        Assert.Throws<ArgumentException>(() => tooShort.Registration.ApplyRuntimeStatics([tall]));
        Assert.Equal(2, tooShort.Registration.Extent.Maximum.Y);
    }

    [Fact]
    public void InvalidShapesPosesAndMaterialsAreRejectedBySetWithoutBackendEdits()
    {
        PhysicsShape[] invalid = [new SphereShape(0), new SphereShape(float.NaN), new CapsuleShape(1, -1),
            new CylinderShape(1, 0), new BoxShape(new(1, -1, 1)), new TriangleMeshShape([Vector3.Zero], [0, 1]),
            new TriangleMeshShape([Vector3.Zero], [0, 1, 2]), new ConvexHullShape([Vector3.Zero]),
            new CompoundShape([])];
        foreach (var shape in invalid)
            Assert.Throws<ArgumentException>(() => RuntimeStaticChange.Set(new("world", "bad"), shape, Pose.Identity));
        Assert.Throws<ArgumentException>(() => RuntimeStaticChange.Set(new("world", "bad"), new SphereShape(1),
            new(Vector3.Zero, default)));
        Assert.Throws<ArgumentException>(() => RuntimeStaticChange.Set(new("world", "bad"), new SphereShape(1),
            Pose.At(new(float.PositiveInfinity))));
        Assert.Throws<ArgumentException>(() => RuntimeStaticChange.Set(new("world", "bad"), new SphereShape(1),
            Pose.Identity, new(float.NaN, 0)));
        CompoundChild[] cycle = new CompoundChild[1];
        var cyclic = new CompoundShape(cycle);
        cycle[0] = new(cyclic, Pose.Identity);
        Assert.Throws<ArgumentException>(() => RuntimeStaticChange.Set(new("world", "bad"), cyclic, Pose.Identity));
        Assert.Throws<NotSupportedException>(() => RuntimeStaticChange.Set(new("world", "bad"), new UnknownShape(), Pose.Identity));
    }

    sealed class UnknownShape : PhysicsShape { }

    [Fact]
    public void ExtentEdgeAcceptsExactBoundsAndRefusesTheNextRepresentableOutsidePoint()
    {
        using var exact = new OrdinaryContactScene(extent: new(new(-1), new(1), 0, 0));
        var owner = new MovementSupportKey("world", "edge");
        exact.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Set(owner, new BoxShape(Vector3.One), Pose.Identity)]);
        long before = exact.Physics.StaticGeometryGeneration;
        Assert.Throws<ArgumentException>(() => exact.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Set(owner,
            new BoxShape(Vector3.One), Pose.At(new(MathF.BitIncrement(0f), 0, 0)))]));
        Assert.Equal(before, exact.Physics.StaticGeometryGeneration);
        using var known = exact.Acquire();
    }

    [Fact]
    public void NestedCompositionRoundsBeforeConservativeExtentContainment()
    {
        PhysicsShape shape = new CompoundShape([new(new CompoundShape([
            new(new BoxShape(Vector3.One), Pose.At(new(0.2f, 0, 0)))]), Pose.At(new(0.1f, 0, 0)))]);
        var change = RuntimeStaticChange.Set(new("world", "rounding"), shape, Pose.Identity);
        using var roundedIn = new OrdinaryContactScene(extent: new(new(-4), new(1.3f, 4, 4), 0, 0));
        long before = roundedIn.Physics.StaticGeometryGeneration;
        Assert.Throws<ArgumentException>(() => roundedIn.Registration.ApplyRuntimeStatics([change]));
        Assert.Equal(before, roundedIn.Physics.StaticGeometryGeneration);
        using var roundedOut = new OrdinaryContactScene(extent: new(new(-4), new(MathF.BitIncrement(1.3f), 4, 4), 0, 0));
        roundedOut.Registration.ApplyRuntimeStatics([change]);
        using var known = roundedOut.Acquire();
    }

    [Fact]
    public void SetRefusesUnsupportedCompoundMeshesAndExcessiveNesting()
    {
        PhysicsShape mesh = new TriangleMeshShape([Vector3.Zero, Vector3.UnitX, Vector3.UnitZ], [0, 1, 2]);
        Assert.Throws<NotSupportedException>(() => RuntimeStaticChange.Set(new("world", "mesh-child"),
            new CompoundShape([new(mesh, Pose.Identity)]), Pose.Identity));
        PhysicsShape nested = new SphereShape(1);
        for (int level = 0; level < 33; level++) nested = new CompoundShape([new(nested, Pose.Identity)]);
        Assert.Throws<ArgumentException>(() => RuntimeStaticChange.Set(new("world", "deep"), nested, Pose.Identity));
    }

    [Fact]
    public void RigidQuaternionBandUsesExactClosedEndpoints()
    {
        // 1^2 + (2^-10)^2 is exactly the installed geometry domain's closed upper endpoint.
        Quaternion edge = new(1, MathF.ScaleB(1, -10), 0, 0);
        _ = RuntimeStaticChange.Set(new("world", "edge"), new SphereShape(1), new(Vector3.Zero, edge));
        // The represented norm exceeds that endpoint even when binary64 summation rounds it away.
        Quaternion outside = edge with { Z = MathF.BitIncrement(0f) };
        Assert.Throws<ArgumentException>(() => RuntimeStaticChange.Set(new("world", "outside"),
            new SphereShape(1), new(Vector3.Zero, outside)));
    }
}
