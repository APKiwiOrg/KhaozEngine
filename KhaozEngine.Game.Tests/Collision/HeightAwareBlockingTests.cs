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

    // The root-cause cases: a domed prop where the walkable surface under the player (0.8) is BELOW the prop's
    // single max solid Top (1.5). Standing on the surface must not be misread as a side hit.
    [Fact]
    public void OnSurfaceBelowMaxTop_NotBlocked()
    {
        var rock = WorldCollider.Cylinder(Vector2.Zero, 1f, top: 1.5f);
        var set = new WorldColliders(new[] { rock });
        // Feet at the surface height (0.8, below the 1.5 peak) -> standing on the dome, NOT pushed.
        Vector2 r = set.Resolve(new Vector2(0.5f, 0f), 0.4f, footY: 0.8f, surfaceTop: 0.8f);
        Assert.Equal(0.5f, r.X, 3);
        Assert.Equal(0f, r.Y, 3);
    }

    [Fact]
    public void BelowSurface_StillBlocked()
    {
        var rock = WorldCollider.Cylinder(Vector2.Zero, 1f, top: 1.5f);
        var set = new WorldColliders(new[] { rock });
        // Feet below the surface they'd stand on (0.2 < 0.8) -> a genuine side hit, pushed out.
        Vector2 r = set.Resolve(new Vector2(0.5f, 0f), 0.4f, footY: 0.2f, surfaceTop: 0.8f);
        Assert.True(new Vector2(r.X, r.Y).Length() >= 1.4f - 0.02f);
    }

    [Fact]
    public void Tree_StillBlocks_EvenWhenStandingOnANeighbourSurface()
    {
        // A thin blocker (tree, top = +inf, no surface of its own). Even with the player's feet at a neighbouring
        // prop's surface height, a tree must always block (it is never standable).
        var tree = WorldCollider.Cylinder(Vector2.Zero, 1f); // default top = +inf
        var set = new WorldColliders(new[] { tree });
        Vector2 r = set.Resolve(new Vector2(0.5f, 0f), 0.4f, footY: 1.5f, surfaceTop: 1.5f);
        Assert.True(new Vector2(r.X, r.Y).Length() >= 1.4f - 0.02f); // never mounted
    }
}
