using System;
using System.Numerics;
using KhaozEngine.Locomotion;
using KhaozEngine.Physics;
using KhaozEngine.Tests.Movement.Fixtures;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class RuntimeStaticCurvedBoundsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ContractingOrientationCannotAdmitANativeRadiusOutsideExtent(bool capsule, bool compound)
    {
        float s = MathF.BitDecrement(1f);
        using var scene = new OrdinaryContactScene(extent: new(new(1, -s, -s), new(3, s, s), 0, 0));
        long generation = scene.Physics.StaticGeometryGeneration;
        RuntimeStaticChange change = Change(capsule, compound);
        Assert.Throws<ArgumentException>(() => scene.Registration.ApplyRuntimeStatics([change]));
        Assert.Equal(generation, scene.Physics.StaticGeometryGeneration);
        Assert.False(scene.Physics.Raycast(new(2, 2, 0), -Vector3.UnitY, 4, out _));
        using var known = scene.Acquire();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void FullNativeRadiusIsAdmittedWithTheOriginalOrientationBits(bool capsule, bool compound)
    {
        using var scene = new OrdinaryContactScene(extent: new(new(1, -1, -1), new(3, 1, 1), 0, 0));
        scene.Registration.ApplyRuntimeStatics([Change(capsule, compound)]);
        Assert.True(scene.Physics.Raycast(new(2, 2, 0), -Vector3.UnitY, 4, out _));
        using var known = scene.Acquire();
    }

    static RuntimeStaticChange Change(bool capsule, bool compound)
    {
        PhysicsShape shape = capsule ? new CapsuleShape(1, 0) : new SphereShape(1);
        Vector3 position = new(2, 0, 0);
        if (compound)
        {
            shape = new CompoundShape([new(shape, Pose.At(position))]);
            position = Vector3.Zero;
        }
        return RuntimeStaticChange.Set(new("world", "round"), shape,
            new(position, new Quaternion(MathF.BitDecrement(1f), 0, 0, 0)));
    }
}
