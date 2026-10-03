using System;
using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavPathPassThroughTests
{
    static NavWaypoint Walk(float x, float z, int layer = 0) => new(new Vector2(x, z), layer);
    static NavWaypoint Hop(float x, float z, int layer = 0) => new(new Vector2(x, z), layer) { Kind = NavWaypointKind.Hop };
    static NavPath Complete(params NavWaypoint[] waypoints) => new(NavPathStatus.Complete, waypoints);

    [Fact]
    public void StraightAndDiagonalInteriorWaypointsArePassThrough()
    {
        NavPath straight = Complete(Walk(0f, 0f), Walk(0.25f, 0f), Walk(0.5f, 0f), Walk(0.75f, 0f));

        Assert.False(straight.IsCollinearPassThrough(0));
        Assert.True(straight.IsCollinearPassThrough(1));
        Assert.True(straight.IsCollinearPassThrough(2));
        Assert.False(straight.IsCollinearPassThrough(3));

        NavPath diagonal = Complete(Walk(0f, 0f), Walk(0.25f, 0.25f), Walk(0.5f, 0.5f));

        Assert.False(diagonal.IsCollinearPassThrough(0));
        Assert.True(diagonal.IsCollinearPassThrough(1));
        Assert.False(diagonal.IsCollinearPassThrough(2));
    }

    [Theory]
    [InlineData(45f)]
    [InlineData(90f)]
    [InlineData(135f)]
    public void TurnsAreNeverPassThrough(float degrees)
    {
        float radians = degrees * MathF.PI / 180f;
        NavPath turn = Complete(Walk(0f, 0f), Walk(1f, 0f), Walk(1f + MathF.Cos(radians), MathF.Sin(radians)));

        Assert.False(turn.IsCollinearPassThrough(1));
    }

    [Fact]
    public void ReversalIsNeverAPassThrough()
    {
        NavPath reversal = Complete(Walk(0f, 0f), Walk(1f, 0f), Walk(0f, 0f));

        Assert.False(reversal.IsCollinearPassThrough(1));
    }

    [Fact]
    public void ZeroLengthSegmentIsNotPassThrough()
    {
        NavPath repeated = Complete(Walk(0f, 0f), Walk(0.25f, 0f), Walk(0.25f, 0f), Walk(0.5f, 0f));

        Assert.False(repeated.IsCollinearPassThrough(1));
        Assert.False(repeated.IsCollinearPassThrough(2));
    }

    [Fact]
    public void LayerChangeOrHopSuccessorIsNotPassThrough()
    {
        NavPath successorOnAnotherLayer = Complete(Walk(0f, 0f), Walk(0.25f, 0f), Walk(0.5f, 0f, layer: 1));
        NavPath predecessorOnAnotherLayer = Complete(Walk(0f, 0f, layer: 1), Walk(0.25f, 0f), Walk(0.5f, 0f));
        NavPath hopSuccessor = Complete(Walk(0f, 0f), Walk(0.25f, 0f), Hop(0.5f, 0f));
        NavPath hopWaypoint = Complete(Walk(0f, 0f), Hop(0.25f, 0f), Walk(0.5f, 0f));

        Assert.False(successorOnAnotherLayer.IsCollinearPassThrough(1));
        Assert.False(predecessorOnAnotherLayer.IsCollinearPassThrough(1));
        Assert.False(hopSuccessor.IsCollinearPassThrough(1));
        Assert.False(hopWaypoint.IsCollinearPassThrough(1));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void IndexOutsideTheListThrows(int index)
    {
        NavPath straight = Complete(Walk(0f, 0f), Walk(0.25f, 0f), Walk(0.5f, 0f));

        Assert.Throws<ArgumentOutOfRangeException>(() => straight.IsCollinearPassThrough(index));
    }
}
