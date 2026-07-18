using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests;

public class CapsuleCollisionTests
{
    // --- Contains (point in capsule), capsule [(0,0),(10,0)] radius 2 ---

    [Fact]
    public void ContainsTrueWhenPointInsideBody()
    {
        // 1 unit above the midpoint, well within radius 2.
        Assert.True(CapsuleCollision.Contains(Vector2.Zero, new Vector2(10f, 0f), 2f, new Vector2(5f, 1f)));
    }

    [Fact]
    public void ContainsFalseWhenPointOutsideBody()
    {
        // 3 units above the segment, outside radius 2.
        Assert.False(CapsuleCollision.Contains(Vector2.Zero, new Vector2(10f, 0f), 2f, new Vector2(5f, 3f)));
    }

    [Fact]
    public void ContainsTrueWhenPointInRoundedEndCap()
    {
        // Past the 'a' endpoint but within the rounded cap: distance to 'a' is 1.5 <= 2.
        Assert.True(CapsuleCollision.Contains(Vector2.Zero, new Vector2(10f, 0f), 2f, new Vector2(-1.5f, 0f)));
    }

    [Fact]
    public void ContainsTrueWhenPointExactlyOnSurface()
    {
        // distance == radius -> touching counts as inside (<=).
        Assert.True(CapsuleCollision.Contains(Vector2.Zero, new Vector2(10f, 0f), 2f, new Vector2(5f, 2f)));
    }

    // --- Circle vs capsule, capsule [(0,0),(10,0)] radius 1 ---

    [Fact]
    public void IntersectsTrueWhenCircleOverlapsCapsule()
    {
        // Centre 1.5 above the segment; 1.5 <= 1 + 1.
        Assert.True(CapsuleCollision.Intersects(Vector2.Zero, new Vector2(10f, 0f), 1f, new Vector2(5f, 1.5f), 1f));
    }

    [Fact]
    public void IntersectsTrueWhenCircleGrazesCapsule()
    {
        // Exactly touching: distance 2 == capsuleRadius + circleRadius (1 + 1).
        Assert.True(CapsuleCollision.Intersects(Vector2.Zero, new Vector2(10f, 0f), 1f, new Vector2(5f, 2f), 1f));
    }

    [Fact]
    public void IntersectsFalseWhenCircleDisjointFromCapsule()
    {
        // distance 2.5 > 1 + 1.
        Assert.False(CapsuleCollision.Intersects(Vector2.Zero, new Vector2(10f, 0f), 1f, new Vector2(5f, 2.5f), 1f));
    }

    // --- Capsule vs capsule ---

    [Fact]
    public void IntersectsTrueWhenParallelCapsulesOverlap()
    {
        // Parallel segments 1.5 apart; 1.5 <= 1 + 1.
        Assert.True(CapsuleCollision.Intersects(
            Vector2.Zero, new Vector2(10f, 0f), 1f,
            new Vector2(0f, 1.5f), new Vector2(10f, 1.5f), 1f));
    }

    [Fact]
    public void IntersectsFalseWhenParallelCapsulesDisjoint()
    {
        // Parallel segments 3 apart; 3 > 1 + 1.
        Assert.False(CapsuleCollision.Intersects(
            Vector2.Zero, new Vector2(10f, 0f), 1f,
            new Vector2(0f, 3f), new Vector2(10f, 3f), 1f));
    }

    [Fact]
    public void IntersectsTrueWhenCapsulesCross()
    {
        // Horizontal and vertical capsules crossing at (5,0): segment distance 0 <= 0.5 + 0.5.
        Assert.True(CapsuleCollision.Intersects(
            Vector2.Zero, new Vector2(10f, 0f), 0.5f,
            new Vector2(5f, -5f), new Vector2(5f, 5f), 0.5f));
    }

    [Fact]
    public void IntersectsFalseWhenCapsulesDisjoint()
    {
        // Two short capsules far apart.
        Assert.False(CapsuleCollision.Intersects(
            Vector2.Zero, new Vector2(1f, 0f), 0.5f,
            new Vector2(10f, 10f), new Vector2(11f, 10f), 0.5f));
    }

    // --- Degenerate a == b must reduce to a circle ---

    [Fact]
    public void DegenerateCapsuleContainsBehavesAsCircle()
    {
        var centre = new Vector2(5f, 0f);
        Assert.True(CapsuleCollision.Contains(centre, centre, 2f, new Vector2(6f, 0f)));  // 1 <= 2
        Assert.False(CapsuleCollision.Contains(centre, centre, 2f, new Vector2(8f, 0f))); // 3 > 2
    }

    [Fact]
    public void DegenerateCapsuleVsCircleMatchesCircleCollision()
    {
        var c = Vector2.Zero;

        // Overlapping: capsule degenerate at origin r2 vs circle (3,0) r2.
        Assert.True(CapsuleCollision.Intersects(c, c, 2f, new Vector2(3f, 0f), 2f));
        Assert.Equal(
            CircleCollision.Intersects(c, 2f, new Vector2(3f, 0f), 2f),
            CapsuleCollision.Intersects(c, c, 2f, new Vector2(3f, 0f), 2f));

        // Disjoint: 5 apart, combined radius 4.
        Assert.False(CapsuleCollision.Intersects(c, c, 2f, new Vector2(5f, 0f), 2f));
        Assert.Equal(
            CircleCollision.Intersects(c, 2f, new Vector2(5f, 0f), 2f),
            CapsuleCollision.Intersects(c, c, 2f, new Vector2(5f, 0f), 2f));
    }

    [Fact]
    public void BothCapsulesDegenerateMatchTwoCircles()
    {
        var a = Vector2.Zero;
        var b = new Vector2(3f, 0f);

        Assert.True(CapsuleCollision.Intersects(a, a, 2f, b, b, 2f)); // 3 <= 2 + 2
        Assert.Equal(
            CircleCollision.Intersects(a, 2f, b, 2f),
            CapsuleCollision.Intersects(a, a, 2f, b, b, 2f));
    }
}
