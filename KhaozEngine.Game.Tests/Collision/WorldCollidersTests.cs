using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests.Collision;

public class WorldCollidersTests
{
    [Fact]
    public void Empty_Resolve_IsNoOp()
    {
        var set = new WorldColliders(new List<WorldCollider>());
        Assert.True(set.IsEmpty);
        Assert.Equal(new Vector2(5f, 7f), set.Resolve(new Vector2(5f, 7f), 0.4f));
    }

    [Fact]
    public void Query_ReturnsNearbyColliders_NotFarOnes()
    {
        var near = WorldCollider.Cylinder(new Vector2(2f, 0f), 0.5f);
        var far = WorldCollider.Cylinder(new Vector2(200f, 0f), 0.5f);
        var set = new WorldColliders(new[] { near, far });
        IReadOnlyList<WorldCollider> hits = set.Query(0f, 0f, 4f);
        Assert.Contains(near, hits);
        Assert.DoesNotContain(far, hits);
    }

    [Fact]
    public void Resolve_PushesCapsuleOutOfTree()
    {
        var set = new WorldColliders(new[] { WorldCollider.Cylinder(Vector2.Zero, 1f) });
        // Capsule r=0.4 trying to stand at (0.5,0) is inside (combined 1.4): pushed out to distance 1.4.
        Vector2 result = set.Resolve(new Vector2(0.5f, 0f), 0.4f);
        Assert.Equal(1.4f, result.X, 2);
        Assert.Equal(0f, result.Y, 3);
    }

    [Fact]
    public void Resolve_SlidesAlongWall_KeepingTangentialMotion()
    {
        // A long thin wall (box) centred at z=1, half-depth 0.5 (faces at z=0.5 and z=1.5). A capsule pushed
        // into the -Z face from below keeps its X (tangent) and is only pushed back in -Z (normal).
        var wall = WorldCollider.Box(new Vector2(0f, 1f), new Vector2(10f, 0.5f), yaw: 0f);
        var set = new WorldColliders(new[] { wall });
        Vector2 result = set.Resolve(new Vector2(3f, 0.8f), 0.4f); // overlaps the -Z face (z=0.5)
        Assert.Equal(3f, result.X, 3);           // tangent X preserved (slide)
        Assert.True(result.Y < 0.8f);            // pushed back in -Z
        Assert.Equal(0.1f, result.Y, 2);         // face at z=0.5, centre must reach 0.5 - r = 0.1
    }

    [Fact]
    public void Resolve_Corner_IteratesOutOfBothColliders()
    {
        // Two cylinders forming a wedge; a capsule jammed between them ends clear of both.
        var a = WorldCollider.Cylinder(new Vector2(-0.6f, 0f), 1f);
        var b = WorldCollider.Cylinder(new Vector2(0.6f, 0f), 1f);
        var set = new WorldColliders(new[] { a, b });
        Vector2 result = set.Resolve(new Vector2(0f, 0.2f), 0.4f);
        Assert.False(a.Resolve(result, 0.4f, out _));
        Assert.False(b.Resolve(result, 0.4f, out _));
    }

    [Fact]
    public void Resolve_FarFromAnything_IsNoOp()
    {
        var set = new WorldColliders(new[] { WorldCollider.Cylinder(new Vector2(100f, 100f), 1f) });
        Assert.Equal(new Vector2(1f, 1f), set.Resolve(new Vector2(1f, 1f), 0.4f));
    }
}
