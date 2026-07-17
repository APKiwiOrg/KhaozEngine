using System.Numerics;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Navigation;

public class NavWaypointTests
{
    [Fact]
    public void DefaultKind_IsWalk()
    {
        var pos = new Vector2(5f, 10f);
        var waypoint = new NavWaypoint(pos, 0);
        Assert.Equal(NavWaypointKind.Walk, waypoint.Kind);
    }

    [Fact]
    public void HopKind_RoundTrips()
    {
        var pos = new Vector2(5f, 10f);
        var waypoint = new NavWaypoint(pos, 0) { Kind = NavWaypointKind.Hop };
        Assert.Equal(NavWaypointKind.Hop, waypoint.Kind);
    }

    [Fact]
    public void EqualityIncludesKind()
    {
        var pos = new Vector2(5f, 10f);
        var walk = new NavWaypoint(pos, 0);
        var hop = new NavWaypoint(pos, 0) { Kind = NavWaypointKind.Hop };
        Assert.NotEqual(walk, hop);
    }
}
