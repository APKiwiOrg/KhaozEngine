using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class RuntimeStaticMeshBoundsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NativeRecenteringCannotEscapeTheDeclaredRawTriangleExtent(bool rotated, bool rebased)
    {
        var extent = rotated ? new OrdinaryMovementExtent(new(0.2f, -1, -1), new(1, 0, 0), 0, 0)
            : new OrdinaryMovementExtent(new(0.2f, 0, 0), new(1, 1, 1), 0, 0);
        using var scene = new OrdinaryContactScene(extent: extent, origin: rebased ? new(0, 8, -8) : Vector3.Zero);
        long generation = scene.Physics.StaticGeometryGeneration;
        var change = RuntimeStaticChange.Set(new("world", "mesh"), Triangle(), Placement(rotated));
        Assert.Throws<ArgumentException>(() => scene.Registration.ApplyRuntimeStatics([change]));
        Assert.Equal(generation, scene.Physics.StaticGeometryGeneration);
        using var known = scene.Acquire();
    }

    [Fact]
    public void ExactUnionOfRawAndNativeIdentityTrianglesIsAdmitted()
    {
        using var scene = new OrdinaryContactScene(extent:
            new(new(MathF.BitDecrement(0.2f), 0, 0), new(1, 1, 1), 0, 0));
        Install(scene, false);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReservedVolumeAdmitsRotatedAndRebasedNativeTriangles(bool rotated, bool rebased)
    {
        using var scene = new OrdinaryContactScene(extent: new(new(-2), new(2), 0, 0),
            origin: rebased ? new(0, 8, -8) : Vector3.Zero);
        Install(scene, rotated);
    }

    static void Install(OrdinaryContactScene scene, bool rotated)
    {
        long generation = scene.Registration.Generation;
        scene.Registration.ApplyRuntimeStatics([RuntimeStaticChange.Set(new("world", "mesh"), Triangle(), Placement(rotated))]);
        Assert.Equal(generation + 1, scene.Registration.Generation);
        using var known = scene.Acquire();
    }

    static Pose Placement(bool rotated) => new(Vector3.Zero, rotated ? new Quaternion(1, 0, 0, 0) : Quaternion.Identity);
    static TriangleMeshShape Triangle() => new([new(0.2f, 0, 0), new(1, 1, 0), new(1, 0, 1)], [0, 1, 2]);
}
