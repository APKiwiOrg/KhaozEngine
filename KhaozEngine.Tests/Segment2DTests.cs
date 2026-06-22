using System.Numerics;
using KhaozEngine.Collision;
using Xunit;

namespace KhaozEngine.Tests;

public class Segment2DTests
{
    private const float Eps = 1e-5f;

    [Fact]
    public void PointBesideMiddleMeasuresPerpendicularDistance()
    {
        // Horizontal segment from (0,0) to (10,0); point sits 3 units above the midpoint.
        float distance = Segment2D.DistanceToSegment(new Vector2(5f, 3f), Vector2.Zero, new Vector2(10f, 0f), out float t);

        Assert.Equal(3f, distance, Eps);
        Assert.Equal(0.5f, t, Eps);
    }

    [Fact]
    public void PointBeyondStartClampsToA()
    {
        // Point is off the 'a' end; closest point on the segment is 'a' itself, so t clamps to 0.
        float distance = Segment2D.DistanceToSegment(new Vector2(-4f, 0f), Vector2.Zero, new Vector2(10f, 0f), out float t);

        Assert.Equal(4f, distance, Eps);
        Assert.Equal(0f, t, Eps);
    }

    [Fact]
    public void PointBeyondEndClampsToB()
    {
        // Point is off the 'b' end; closest point on the segment is 'b' itself, so t clamps to 1.
        float distance = Segment2D.DistanceToSegment(new Vector2(13f, 0f), Vector2.Zero, new Vector2(10f, 0f), out float t);

        Assert.Equal(3f, distance, Eps);
        Assert.Equal(1f, t, Eps);
    }

    [Fact]
    public void PointOnSegmentHasZeroDistance()
    {
        // Point lies exactly on the segment a quarter of the way along.
        float distance = Segment2D.DistanceToSegment(new Vector2(2.5f, 0f), Vector2.Zero, new Vector2(10f, 0f), out float t);

        Assert.Equal(0f, distance, Eps);
        Assert.Equal(0.25f, t, Eps);
    }

    [Fact]
    public void DegenerateZeroLengthSegmentReturnsDistanceToPointAndTZero()
    {
        // a == b: there is no direction to project onto, so distance is |p - a| and t is 0.
        var a = new Vector2(4f, 7f);
        float distance = Segment2D.DistanceToSegment(new Vector2(7f, 11f), a, a, out float t);

        Assert.Equal(5f, distance, Eps); // (3,4) -> length 5
        Assert.Equal(0f, t, Eps);
    }

    [Fact]
    public void SweptPathCatchesTargetTheRawEndpointsMiss()
    {
        // Tunneling case: a fast bullet sweeps from (0,0) to (100,0) in one frame. A thin enemy of radius 2
        // sits at (50,1) - dead centre of the path. Sampling only the endpoints misses it entirely, but the
        // swept segment distance detects the mid-segment hit.
        var from = new Vector2(0f, 0f);
        var to = new Vector2(100f, 0f);
        var enemy = new Vector2(50f, 1f);
        const float enemyRadius = 2f;

        // Raw endpoints are nowhere near the enemy.
        Assert.True((from - enemy).Length() > enemyRadius);
        Assert.True((to - enemy).Length() > enemyRadius);

        // The swept segment passes within the enemy's radius, so the hit is caught.
        float distance = Segment2D.DistanceToSegment(enemy, from, to, out float t);
        Assert.True(distance <= enemyRadius);
        Assert.Equal(0.5f, t, Eps); // closest approach is at the midpoint, usable for ordering hits along the path
    }

    [Fact]
    public void SegmentToSegmentParallelSeparatedMeasuresGap()
    {
        // Two horizontal segments 3 units apart; closest approach is the perpendicular gap.
        float distance = Segment2D.SegmentToSegmentDistance(
            Vector2.Zero, new Vector2(10f, 0f), new Vector2(0f, 3f), new Vector2(10f, 3f));

        Assert.Equal(3f, distance, Eps);
    }

    [Fact]
    public void SegmentToSegmentCrossingHasZeroDistance()
    {
        // A horizontal and a vertical segment that intersect at (5,0): distance is exactly 0.
        float distance = Segment2D.SegmentToSegmentDistance(
            Vector2.Zero, new Vector2(10f, 0f), new Vector2(5f, -5f), new Vector2(5f, 5f));

        Assert.Equal(0f, distance, Eps);
    }

    [Fact]
    public void SegmentToSegmentDisjointMeasuresClosestEndpoints()
    {
        // Non-parallel, non-touching: seg1's 'b' end (1,0) is closest to seg2's lower end (3,0), gap 2.
        float distance = Segment2D.SegmentToSegmentDistance(
            Vector2.Zero, new Vector2(1f, 0f), new Vector2(3f, 0f), new Vector2(3f, 4f));

        Assert.Equal(2f, distance, Eps);
    }

    [Fact]
    public void SegmentToSegmentBothDegenerateReducesToPointDistance()
    {
        // Both segments are points: distance is |p1 - p2| = |(3,4)| = 5.
        float distance = Segment2D.SegmentToSegmentDistance(
            Vector2.Zero, Vector2.Zero, new Vector2(3f, 4f), new Vector2(3f, 4f));

        Assert.Equal(5f, distance, Eps);
    }

    [Fact]
    public void SegmentToSegmentOneDegenerateReducesToPointToSegment()
    {
        // seg1 is the point (5,3); seg2 is the x-axis span [0,10]. Matches DistanceToSegment exactly.
        var point = new Vector2(5f, 3f);
        var a = Vector2.Zero;
        var b = new Vector2(10f, 0f);

        float viaSegSeg = Segment2D.SegmentToSegmentDistance(point, point, a, b);
        float viaPointSeg = Segment2D.DistanceToSegment(point, a, b, out _);

        Assert.Equal(3f, viaSegSeg, Eps);
        Assert.Equal(viaPointSeg, viaSegSeg, Eps);
    }
}
