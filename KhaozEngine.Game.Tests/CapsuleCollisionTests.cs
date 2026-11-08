using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests;

public class CapsuleCollisionTests
{
    // --- Contains (point in capsule), capsule [(0,0),(10,0)] radius 2 ---

    [Theory]
    [InlineData(5f, 1f, true)] // Inside the body.
    [InlineData(5f, 3f, false)] // Outside the body.
    [InlineData(-1.5f, 0f, true)] // Inside the rounded end cap.
    [InlineData(5f, 2f, true)] // Exactly on the surface.
    public void ContainsRespectsBodyCapsAndSurface(float x, float y, bool expected)
    {
        Assert.Equal(expected, CapsuleCollision.Contains(Vector2.Zero, new Vector2(10f, 0f), 2f, new Vector2(x, y)));
    }

    // --- Circle vs capsule, capsule [(0,0),(10,0)] radius 1 ---

    [Theory]
    [InlineData(1.5f, true)] // Overlap.
    [InlineData(2f, true)] // Exact contact at the combined radius.
    [InlineData(2.5f, false)] // Disjoint.
    public void CircleIntersectionRespectsCombinedRadius(float centreY, bool expected)
    {
        Assert.Equal(expected, CapsuleCollision.Intersects(
            Vector2.Zero, new Vector2(10f, 0f), 1f, new Vector2(5f, centreY), 1f));
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

    [Theory]
    [InlineData(10f, 1f, 0f, 3f, 10f, 3f)] // Parallel, perpendicular gap.
    [InlineData(1f, 0.5f, 10f, 10f, 11f, 10f)] // Distant endpoints.
    public void DisjointCapsulesDoNotIntersect(float endX, float radius, float otherStartX,
        float otherStartY, float otherEndX, float otherEndY)
    {
        Assert.False(CapsuleCollision.Intersects(
            Vector2.Zero, new Vector2(endX, 0f), radius,
            new Vector2(otherStartX, otherStartY), new Vector2(otherEndX, otherEndY), radius));
    }

    [Fact]
    public void IntersectsTrueWhenCapsulesCross()
    {
        // Horizontal and vertical capsules crossing at (5,0): segment distance 0 <= 0.5 + 0.5.
        Assert.True(CapsuleCollision.Intersects(
            Vector2.Zero, new Vector2(10f, 0f), 0.5f,
            new Vector2(5f, -5f), new Vector2(5f, 5f), 0.5f));
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
