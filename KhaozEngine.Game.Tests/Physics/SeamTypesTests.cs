using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class SeamTypesTests
{
    [Fact]
    public void Pose_At_IsIdentityOrientation()
    {
        Pose p = Pose.At(new Vector3(1f, 2f, 3f));
        Assert.Equal(new Vector3(1f, 2f, 3f), p.Position);
        Assert.Equal(Quaternion.Identity, p.Orientation);
    }

    [Fact]
    public void CapsuleShape_TotalHeight_IsLengthPlusTwoRadii()
    {
        var c = new CapsuleShape(radius: 0.4f, length: 1.0f);
        Assert.Equal(0.4f, c.Radius);
        Assert.Equal(1.0f, c.Length);
        // total height = length + 2*radius = 1.8 (a 1.8 m character)
        Assert.Equal(1.8f, c.Length + 2f * c.Radius, 3);
    }

    [Fact]
    public void Material_Default_IsFullFrictionNoBounce()
    {
        Assert.Equal(1f, PhysicsMaterial.Default.Friction);
        Assert.Equal(0f, PhysicsMaterial.Default.Restitution);
    }

    [Fact]
    public void Shapes_ExposeTheirData()
    {
        var hull = new ConvexHullShape(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ });
        Assert.Equal(4, hull.Points.Length);
        var mesh = new TriangleMeshShape(new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitZ }, new[] { 0, 1, 2 });
        Assert.Equal(3, mesh.Indices.Length);
    }
}
