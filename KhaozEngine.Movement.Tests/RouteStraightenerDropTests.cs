using System.Linq;
using System.Numerics;
using KhaozEngine.Movement;
using KhaozEngine.Navigation;
using Xunit;

namespace KhaozEngine.Tests.Movement;

public class RouteStraightenerDropTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EvenAPermissiveGuardCannotRemoveDropAnchors(bool consecutive)
    {
        float[] heights = consecutive ? new float[] { 4, 4, 2, 0, 0 } : new float[] { 4, 4, 0, 0 };
        int width = heights.Length;
        var grid = NavGrid.FromBlockedSurfaces(width, 1, 1, 0, 0, new bool[width], heights);
        NavLink[] links = consecutive
            ? new[] { new NavLink(0, 1, 0, 0, 2, 0) { Kind = NavLinkKind.Drop }, new NavLink(0, 2, 0, 0, 3, 0) { Kind = NavLinkKind.Drop } }
            : new[] { new NavLink(0, 1, 0, 0, 2, 0) { Kind = NavLinkKind.Drop } };
        var space = new NavSpace(new[] { grid }, links);
        var exits = new byte[width];
        exits[0] = exits[width - 2] = 1;
        var graph = new NavTraversalGraph(space, 0, 1, new[] {
            new NavTraversalLayer(width, 1, Enumerable.Repeat(true, width).ToArray(), exits) }, links);
        var inner = new GridPathPlanner(space, graph);
        var straightener = new RouteStraightener(inner, space, (_, _) => true);
        Vector3 start = new(0.5f, 4, 0.5f), goal = new(width - 0.5f, 0, 0.5f);
        NavPath raw = inner.FindPath(start, goal, 0, PathQueryBudget.Default);
        NavPath path = straightener.FindPath(start, goal, 0, PathQueryBudget.Default);
        Assert.Equal(NavPathStatus.Complete, path.Status);
        Assert.Equal(raw.Waypoints, path.Waypoints);
        Assert.Equal(consecutive ? 2 : 1, path.Waypoints.Count(w => w.Kind == NavWaypointKind.Drop));
        Assert.Equal(new Vector2(1.5f, 0.5f), path.Waypoints[0].Position);
        Assert.Equal(NavWaypointKind.Walk, path.Waypoints[^1].Kind);
    }
}
