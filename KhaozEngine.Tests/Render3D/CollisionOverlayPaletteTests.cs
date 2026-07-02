using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Physics;
using KhaozEngine.Render3D.Debug;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public class CollisionOverlayPaletteTests
{
    [Fact]
    public void KindOf_maps_every_shape_type()
    {
        Assert.Equal(CollisionShapeKind.Box, CollisionOverlayPalette.KindOf(new BoxShape(Vector3.One)));
        Assert.Equal(CollisionShapeKind.Sphere, CollisionOverlayPalette.KindOf(new SphereShape(1f)));
        Assert.Equal(CollisionShapeKind.Capsule, CollisionOverlayPalette.KindOf(new CapsuleShape(0.5f, 1f)));
        Assert.Equal(CollisionShapeKind.Cylinder, CollisionOverlayPalette.KindOf(new CylinderShape(0.5f, 1f)));
        Assert.Equal(CollisionShapeKind.ConvexHull, CollisionOverlayPalette.KindOf(new ConvexHullShape(new[] { Vector3.Zero })));
        Assert.Equal(CollisionShapeKind.TriangleMesh, CollisionOverlayPalette.KindOf(new TriangleMeshShape(new[] { Vector3.Zero }, new[] { 0 })));
    }

    [Fact]
    public void Default_colors_are_distinct_and_translucent()
    {
        var p = new CollisionOverlayPalette();
        var kinds = Enum.GetValues<CollisionShapeKind>();
        var seen = new HashSet<string>();
        foreach (var k in kinds)
        {
            var c = p.For(k);
            Assert.InRange(c.A, 0.01f, 0.9f); // translucent
            Assert.True(seen.Add($"{c.R:F2},{c.G:F2},{c.B:F2}"), $"duplicate hue for {k}");
        }
    }

    [Fact]
    public void Palette_color_is_overridable()
    {
        var p = new CollisionOverlayPalette();
        var custom = new KhaozEngine.Primitives.Color(0.1f, 0.2f, 0.3f, 0.4f);
        p[CollisionShapeKind.Box] = custom;
        Assert.Equal(custom, p.For(CollisionShapeKind.Box));
    }
}
