using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests.Collision;

public class WorldColliderTests
{
    [Fact]
    public void CylinderShape_Place_ScalesRadius()
    {
        WorldCollider wc = ColliderShape.Cylinder(0.5f).Place(new Vector2(3f, 4f), scale: 2f, yaw: 1f);
        Assert.Equal(ColliderKind.Cylinder, wc.Kind);
        Assert.Equal(new Vector2(3f, 4f), wc.Center);
        Assert.Equal(1f, wc.Radius, 4); // 0.5 * 2
    }

    [Fact]
    public void BoxShape_Place_ScalesHalfExtents_AndCarriesYaw()
    {
        WorldCollider wc = ColliderShape.Box(2f, 1f).Place(new Vector2(5f, 6f), scale: 1.5f, yaw: 0.7f);
        Assert.Equal(ColliderKind.Box, wc.Kind);
        Assert.Equal(new Vector2(5f, 6f), wc.Center);
        Assert.Equal(3f, wc.HalfExtents.X, 4);  // 2 * 1.5
        Assert.Equal(1.5f, wc.HalfExtents.Y, 4); // 1 * 1.5
        Assert.Equal(0.7f, wc.Yaw, 4);
    }

    [Fact]
    public void Cylinder_Resolve_DispatchesToCircleCircle()
    {
        WorldCollider wc = WorldCollider.Cylinder(Vector2.Zero, 1f);
        Assert.True(wc.Resolve(new Vector2(1.5f, 0f), 1f, out Vector2 push));
        Assert.Equal(0.5f, push.X, 3);
    }

    [Fact]
    public void Box_Resolve_DispatchesToOrientedBox()
    {
        WorldCollider wc = WorldCollider.Box(Vector2.Zero, new Vector2(0.5f, 0.5f), yaw: 0f);
        Assert.True(wc.Resolve(new Vector2(1.2f, 0f), 1f, out Vector2 push));
        Assert.Equal(0.3f, push.X, 3);
    }

    [Fact]
    public void Box_BoundingRadius_IsHalfDiagonal()
    {
        WorldCollider wc = WorldCollider.Box(Vector2.Zero, new Vector2(3f, 4f), yaw: 0f);
        Assert.Equal(5f, wc.BoundingRadius, 3); // sqrt(3^2+4^2)
    }

    [Fact]
    public void Cylinder_BoundingRadius_IsRadius()
    {
        Assert.Equal(2f, WorldCollider.Cylinder(Vector2.Zero, 2f).BoundingRadius, 3);
    }
}
