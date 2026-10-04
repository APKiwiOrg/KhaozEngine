using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Drops cell-centre waypoints from planned routes where the segment guard accepts the centre line and
/// both side lines, so a body walks straight instead of along the grid's 45 and 90 degree staircase. Waypoints are
/// only dropped, never created or moved, and the inner path's status is kept.</summary>
internal sealed class RouteStraightener : IRegionPathPlanner
{
    /// <summary>How far a side line sits from the centre line, in cells of the anchor's layer. Just under half a cell
    /// keeps a side line off the cell boundary, so an axis-aligned segment judges both sides alike while an oblique
    /// one still enters both neighbour rows where the body can.</summary>
    internal const float SideOffsetCells = 0.499f;

    private readonly IRegionPathPlanner _inner;
    private readonly NavSpace _space;
    private readonly Func<Vector3, Vector3, bool> _allowsSegment;
    private bool _fallBack;

    public RouteStraightener(IRegionPathPlanner inner, NavSpace space, Func<Vector3, Vector3, bool> allowsSegment)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _space = space ?? throw new ArgumentNullException(nameof(space));
        _allowsSegment = allowsSegment ?? throw new ArgumentNullException(nameof(allowsSegment));
    }

    /// <summary>The path most recently returned when it dropped at least one waypoint, else null.</summary>
    internal NavPath? LastStraightened { get; private set; }

    /// <summary>The next plan returns the inner route unchanged. The plan after it straightens again.</summary>
    internal void FallBackOnce() => _fallBack = true;

    public NavPath FindPath(Vector3 start, Vector3 goal, float agentRadius, PathQueryBudget budget)
        => Straighten(start, _inner.FindPath(start, goal, agentRadius, budget));

    public NavPath FindPath(Vector3 start, NavGoalRegion goal, float agentRadius, PathQueryBudget budget)
        => Straighten(start, _inner.FindPath(start, goal, agentRadius, budget));

    private NavPath Straighten(Vector3 start, NavPath inner)
    {
        LastStraightened = null;
        if (_fallBack)
        {
            _fallBack = false;
            return inner;
        }
        if (inner.Status == NavPathStatus.Unreachable) return inner;

        IReadOnlyList<NavWaypoint> wp = inner.Waypoints;
        int n = wp.Count;
        if (n < 2) return inner;
        int startLayer = _space.LayerAt(start);
        var kept = new List<NavWaypoint>(n);
        Vector3 anchorFeet = start;
        bool anchorKnown = true;
        int anchorLayer = startLayer;
        // a is the anchor's index (-1 = the query start feet). The next waypoint is a raw planner edge and is kept
        // unchecked.
        for (int a = -1; a < n - 1;)
        {
            int best = a + 1;
            for (int j = a + 2; j < n && anchorKnown && !Mandatory(wp, best, startLayer); j++)
            {
                if (wp[j].Kind != NavWaypointKind.Walk || wp[j].Layer != anchorLayer) break;
                if (!TryFeet(wp[j], out Vector3 feet) || !Clear(anchorFeet, feet, anchorLayer)) break;
                best = j;
            }
            kept.Add(wp[best]);
            a = best;
            anchorKnown = TryFeet(wp[best], out anchorFeet);
            anchorLayer = wp[best].Layer;
        }
        if (kept.Count == n) return inner;
        var straightened = new NavPath(inner.Status, kept);
        LastStraightened = straightened;
        return straightened;
    }

    // The final waypoint carries region membership, a hop needs its start and landing, and a layer change needs
    // both ends, so a scan never passes any of them.
    private static bool Mandatory(IReadOnlyList<NavWaypoint> wp, int i, int startLayer)
    {
        if (i == wp.Count - 1 || wp[i].Kind == NavWaypointKind.Hop) return true;
        int previousLayer = i == 0 ? startLayer : wp[i - 1].Layer;
        return wp[i].Layer != previousLayer || wp[i + 1].Kind == NavWaypointKind.Hop || wp[i + 1].Layer != wp[i].Layer;
    }

    // A waypoint on a heightless or blocked cell has no feet to check, so no shortcut reaches or leaves it.
    private bool TryFeet(NavWaypoint waypoint, out Vector3 feet)
    {
        NavGrid grid = _space.Layers[waypoint.Layer];
        (int x, int z) = grid.CellOf(waypoint.Position.X, waypoint.Position.Y);
        float? height = grid.SurfaceHeightAt(x, z);
        feet = new Vector3(waypoint.Position.X, height ?? 0f, waypoint.Position.Y);
        return height.HasValue;
    }

    private bool Clear(Vector3 from, Vector3 to, int layer)
    {
        if (!_allowsSegment(from, to)) return false;
        double dx = (double)to.X - from.X, dz = (double)to.Z - from.Z;
        double length = Math.Sqrt(dx * dx + dz * dz);
        if (length == 0d) return true;
        double scale = SideOffsetCells * (double)_space.Layers[layer].CellSize / length;
        double ox = -dz * scale, oz = dx * scale;
        return _allowsSegment(Offset(from, ox, oz), Offset(to, ox, oz)) &&
            _allowsSegment(Offset(from, -ox, -oz), Offset(to, -ox, -oz));
    }

    private static Vector3 Offset(Vector3 feet, double ox, double oz)
        => new((float)(feet.X + ox), feet.Y, (float)(feet.Z + oz));
}
