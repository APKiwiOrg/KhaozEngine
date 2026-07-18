using System.Numerics;
using KhaozEngine.Physics;
using Xunit;

namespace KhaozEngine.Tests.Physics;

public class PhysicsShapeScaleTests
{
    [Fact]
    public void Scale1_ReturnsSameInstance()
    {
        var box = new BoxShape(new Vector3(1, 2, 3));
        Assert.Same(box, PhysicsShapeScale.Uniform(box, 1f));
    }

    [Fact]
    public void Box_ScalesHalfExtents()
    {
        var box = new BoxShape(new Vector3(1, 2, 3));
        var scaled = Assert.IsType<BoxShape>(PhysicsShapeScale.Uniform(box, 2f));
        Assert.Equal(new Vector3(2, 4, 6), scaled.HalfExtents);
    }

    [Fact]
    public void Compound_ScalesChildGeometryAndPosePositionNotOrientation()
    {
        Quaternion rot = Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f);
        var child = new CompoundChild(new BoxShape(new Vector3(1, 1, 1)), new Pose(new Vector3(4, 0, 0), rot));
        var compound = new CompoundShape(new[] { child });

        var scaled = Assert.IsType<CompoundShape>(PhysicsShapeScale.Uniform(compound, 3f));
        Assert.Single(scaled.Children);
        var sc = scaled.Children[0];
        Assert.Equal(new Vector3(3, 3, 3), Assert.IsType<BoxShape>(sc.Shape).HalfExtents);
        Assert.Equal(new Vector3(12, 0, 0), sc.Local.Position);   // pose position scaled
        Assert.Equal(rot, sc.Local.Orientation);                  // orientation preserved
    }

    [Fact]
    public void ConvexHull_ScalesPoints()
    {
        var hull = new ConvexHullShape(new[] { new Vector3(1, 0, 0), new Vector3(0, 2, 0), new Vector3(0, 0, 3), Vector3.Zero });
        var scaled = Assert.IsType<ConvexHullShape>(PhysicsShapeScale.Uniform(hull, 2f));
        Assert.Equal(new Vector3(2, 0, 0), scaled.Points[0]);
    }
}
