using System.Numerics;
using KhaozEngine.NetWorld;
using Xunit;

namespace KhaozEngine.Tests.NetWorld;

public class WorldBoundsTests
{
    [Fact]
    public void Circle_contains_inside_not_outside_boundary_inclusive()
    {
        var b = new CircleBounds(new Vector2(0f, 0f), 10f);
        Assert.True(b.Contains(0f, 0f));
        Assert.True(b.Contains(10f, 0f));     // on the boundary counts as inside
        Assert.False(b.Contains(11f, 0f));
    }

    [Fact]
    public void Circle_clamp_projects_outside_onto_boundary_and_is_idempotent()
    {
        var b = new CircleBounds(new Vector2(0f, 0f), 10f);
        Vector2 inside = b.Clamp(3f, 4f);
        Assert.Equal(new Vector2(3f, 4f), inside);                       // inside unchanged
        Vector2 onEdge = b.Clamp(30f, 40f);                             // dist 50 -> onto r=10
        Assert.Equal(10f, onEdge.Length(), 3);
        Assert.Equal(6f, onEdge.X, 3);                                  // 30/50*10
        Assert.Equal(8f, onEdge.Y, 3);
        Assert.Equal(onEdge, b.Clamp(onEdge.X, onEdge.Y));             // idempotent on the boundary
    }

    [Fact]
    public void Circle_clamp_at_centre_is_safe()
    {
        var b = new CircleBounds(new Vector2(5f, 5f), 10f);
        Assert.Equal(new Vector2(5f, 5f), b.Clamp(5f, 5f));
    }

    [Fact]
    public void Rect_contains_and_clamp_per_axis()
    {
        var b = new RectBounds(-10f, -5f, 10f, 5f);
        Assert.True(b.Contains(0f, 0f));
        Assert.False(b.Contains(20f, 0f));
        Assert.Equal(new Vector2(10f, 0f), b.Clamp(20f, 0f));          // x clamped, z kept
        Assert.Equal(new Vector2(3f, -5f), b.Clamp(3f, -50f));         // z clamped, x kept (slide)
        Assert.Equal(new Vector2(3f, 2f), b.Clamp(3f, 2f));           // inside unchanged
    }
}
