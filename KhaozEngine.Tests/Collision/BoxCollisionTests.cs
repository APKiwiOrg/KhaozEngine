using System;
using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests.Collision;

public class BoxCollisionTests
{
    [Fact]
    public void CircleAabb_HeadOn_PushesOutAlongShortestAxis()
    {
        // Circle r=1 centred 1.2 right of a 1x1 (half 0.5) box at origin: nearest face is +X at x=0.5,
        // gap from centre to face = 0.7, overlap depth = r - gap = 0.3 along +X.
        bool hit = BoxCollision.ResolveCircleAabb(new Vector2(1.2f, 0f), 1f, Vector2.Zero, new Vector2(0.5f, 0.5f), out Vector2 push);
        Assert.True(hit);
        Assert.Equal(0.3f, push.X, 3);
        Assert.Equal(0f, push.Y, 3);
    }

    [Fact]
    public void CircleAabb_Glancing_PushesOnlyPerpendicular_SoTangentSurvives()
    {
        // Circle just clipping the top face (+Y) while travelling along X: push is purely +Y (the tangent X is untouched).
        bool hit = BoxCollision.ResolveCircleAabb(new Vector2(0f, 0.6f), 0.5f, Vector2.Zero, new Vector2(0.5f, 0.5f), out Vector2 push);
        Assert.True(hit);
        Assert.Equal(0f, push.X, 3);
        Assert.Equal(0.4f, push.Y, 3); // r(0.5) - gap(0.1)
    }

    [Fact]
    public void CircleAabb_Corner_PushesAlongDiagonalNormal()
    {
        // Near the +X/+Y corner (0.5,0.5): centre at (0.8,0.8), closest point is the corner, dir = (1,1)/sqrt2.
        bool hit = BoxCollision.ResolveCircleAabb(new Vector2(0.8f, 0.8f), 0.5f, Vector2.Zero, new Vector2(0.5f, 0.5f), out Vector2 push);
        Assert.True(hit);
        float cornerDist = MathF.Sqrt(0.3f * 0.3f + 0.3f * 0.3f); // ~0.4243
        float depth = 0.5f - cornerDist;
        Assert.Equal(depth / MathF.Sqrt(2f), push.X, 3);
        Assert.Equal(depth / MathF.Sqrt(2f), push.Y, 3);
        Assert.True(push.X > 0 && push.Y > 0);
    }

    [Fact]
    public void CircleAabb_CentreInside_PushesOutNearestFace()
    {
        // Centre at (0.2,0) inside a half-0.5 box, r=0.1: nearest face +X. Must exit fully: centre -> 0.5 + r = 0.6.
        bool hit = BoxCollision.ResolveCircleAabb(new Vector2(0.2f, 0f), 0.1f, Vector2.Zero, new Vector2(0.5f, 0.5f), out Vector2 push);
        Assert.True(hit);
        Assert.Equal(0.4f, push.X, 3); // 0.6 - 0.2
        Assert.Equal(0f, push.Y, 3);
    }

    [Fact]
    public void CircleAabb_Clear_ReturnsFalse()
    {
        Assert.False(BoxCollision.ResolveCircleAabb(new Vector2(2f, 0f), 0.5f, Vector2.Zero, new Vector2(0.5f, 0.5f), out Vector2 push));
        Assert.Equal(Vector2.Zero, push);
    }

    [Fact]
    public void CircleOrientedBox_45Deg_PushesAlongRotatedNormal()
    {
        // A box rotated 45 deg: its +X local face normal points to (cos45, sin45) in world. A circle approaching
        // along that world direction is pushed back along it.
        float yaw = MathF.PI / 4f;
        var boxHalf = new Vector2(0.5f, 0.5f);
        Vector2 faceDir = new(MathF.Cos(yaw), MathF.Sin(yaw));
        Vector2 c = faceDir * (0.5f + 0.4f); // gap 0.4 from face, r=0.5 -> depth 0.1
        bool hit = BoxCollision.ResolveCircleOrientedBox(c, 0.5f, Vector2.Zero, boxHalf, yaw, out Vector2 push);
        Assert.True(hit);
        Assert.Equal(0.1f, push.Length(), 2);
        Assert.True(Vector2.Dot(Vector2.Normalize(push), faceDir) > 0.99f);
    }

    [Fact]
    public void CircleOrientedBox_EqualsAabb_WhenYawZero()
    {
        BoxCollision.ResolveCircleAabb(new Vector2(1.2f, 0f), 1f, Vector2.Zero, new Vector2(0.5f, 0.5f), out Vector2 a);
        BoxCollision.ResolveCircleOrientedBox(new Vector2(1.2f, 0f), 1f, Vector2.Zero, new Vector2(0.5f, 0.5f), 0f, out Vector2 b);
        Assert.Equal(a.X, b.X, 4);
        Assert.Equal(a.Y, b.Y, 4);
    }

    [Fact]
    public void CircleCircle_PushesApartAlongCentreLine()
    {
        // Two circles r=1, centres 1.5 apart -> overlap depth 0.5 along the line.
        bool hit = BoxCollision.ResolveCircleCircle(new Vector2(1.5f, 0f), 1f, Vector2.Zero, 1f, out Vector2 push);
        Assert.True(hit);
        Assert.Equal(0.5f, push.X, 3);
        Assert.Equal(0f, push.Y, 3);
    }

    [Fact]
    public void CircleCircle_Clear_ReturnsFalse()
    {
        Assert.False(BoxCollision.ResolveCircleCircle(new Vector2(3f, 0f), 1f, Vector2.Zero, 1f, out _));
    }
}
