using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests.Collision;

public class HeightAwareBlockingTests
{
    [Fact]
    public void BelowTop_StillBlocked()
    {
        var rock = WorldCollider.Cylinder(Vector2.Zero, 1f, top: 1.5f);
        var set = new WorldColliders(new[] { rock });
        // Feet at y=0 (at the side) -> pushed out.
        Vector2 r = set.Resolve(new Vector2(0.5f, 0f), 0.4f, footY: 0f);
        Assert.True(new Vector2(r.X, r.Y).Length() >= 1.4f - 0.02f);
    }

    [Fact]
    public void AtOrAboveTop_NotBlocked()
    {
        var rock = WorldCollider.Cylinder(Vector2.Zero, 1f, top: 1.5f);
        var set = new WorldColliders(new[] { rock });
        // Standing on top (feet at the rock top) -> NOT pushed (you stay where you are).
        Vector2 r = set.Resolve(new Vector2(0.5f, 0f), 0.4f, footY: 1.5f);
        Assert.Equal(0.5f, r.X, 3);
        Assert.Equal(0f, r.Y, 3);
    }

    [Fact]
    public void DefaultTop_AlwaysBlocks_LikeATree()
    {
        var tree = WorldCollider.Cylinder(Vector2.Zero, 1f); // default top = +inf
        var set = new WorldColliders(new[] { tree });
        Vector2 r = set.Resolve(new Vector2(0.5f, 0f), 0.4f, footY: 100f);
        Assert.True(new Vector2(r.X, r.Y).Length() >= 1.4f - 0.02f); // never mounted
    }

    [Fact]
    public void OldResolve_Unchanged()
    {
        var rock = WorldCollider.Cylinder(Vector2.Zero, 1f, top: 1.5f);
        var set = new WorldColliders(new[] { rock });
        Vector2 r = set.Resolve(new Vector2(0.5f, 0f), 0.4f); // height-agnostic overload
        Assert.True(new Vector2(r.X, r.Y).Length() >= 1.4f - 0.02f);
    }
}
