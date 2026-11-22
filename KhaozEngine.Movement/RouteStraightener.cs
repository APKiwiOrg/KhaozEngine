using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>Drops cell-centre waypoints from planned routes where the segment guard accepts the centre line and
/// both side lines, so a body walks straight instead of along the grid's 45 and 90 degree staircase. Waypoints are
/// only dropped, never created or moved, and the inner path's status is kept.</summary>
internal sealed partial class RouteStraightener : IIncrementalRegionPathPlanner
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
        if (BeginStraightening()) return inner;
        if (!CanStraighten(inner)) return inner;

        IReadOnlyList<NavWaypoint> wp = inner.Waypoints;
        int n = wp.Count;
        if (!HasShortcutCandidates(n)) return inner;
        // Keep malformed planner output intact for the caller's typed admission checks. A shortcut must
        // neither hide an invalid waypoint nor index its unknown layer while seeking an anchor.
        for (int i = 0; i < n; i++)
            if (!TryFeet(wp[i], out _)) return inner;
        int startLayer = _space.LayerAt(start);
        var kept = new List<NavWaypoint>(n);
        Vector3 anchorFeet = start;
        bool anchorKnown = true;
        int anchorLayer = startLayer;
        // a is the anchor's index (-1 = the query start feet). The next waypoint is a raw planner edge and is kept
        // unchecked.
        for (int a = -1; a < n - 1;)
        {
            int best = NextEdge(a);
            for (int j = FirstShortcut(a); j < n && anchorKnown && !Mandatory(wp, best, startLayer); j++)
            {
                if (!IsWalk(wp[j]) || !OnLayer(wp[j], anchorLayer)) break;
                if (!TryFeet(wp[j], out Vector3 feet) || !Clear(anchorFeet, feet, anchorLayer)) break;
                best = j;
            }
            kept.Add(wp[best]);
            a = best;
            anchorKnown = TryFeet(wp[best], out anchorFeet);
            anchorLayer = wp[best].Layer;
        }
        if (!DroppedWaypoints(kept.Count, n)) return inner;
        var straightened = new NavPath(inner.Status, kept);
        RecordStraightened(straightened);
        return straightened;
    }

    /// <summary>Keeps the region endpoint and both ends of a special transition or layer change.</summary>
    /// <remarks>Both drivers test the best waypoint before trying the immediately following candidate.
    /// On a validated route, this rule refuses the predecessor of a special waypoint or layer change.
    /// Passing it keeps the best and candidate on the anchor's layer and the candidate a Walk waypoint,
    /// so the subsequent candidate kind and layer checks cannot refuse a shortcut on their own.</remarks>
    internal static bool Mandatory(IReadOnlyList<NavWaypoint> wp, int i, int startLayer)
    {
        if (i == wp.Count - 1 || wp[i].Kind != NavWaypointKind.Walk) return true;
        int previousLayer = i == 0 ? startLayer : wp[i - 1].Layer;
        return wp[i].Layer != previousLayer || wp[i + 1].Kind != NavWaypointKind.Walk || wp[i + 1].Layer != wp[i].Layer;
    }

    // A waypoint on a heightless or blocked cell has no feet to check, so no shortcut reaches or leaves it.
    internal bool TryFeet(NavWaypoint waypoint, out Vector3 feet)
    {
        feet = default;
        if ((uint)waypoint.Layer >= (uint)_space.Layers.Count ||
            !float.IsFinite(waypoint.Position.X) || !float.IsFinite(waypoint.Position.Y)) return false;
        NavGrid grid = _space.Layers[waypoint.Layer];
        (int x, int z) = grid.CellOf(waypoint.Position.X, waypoint.Position.Y);
        float? height = grid.RestingHeightAt(x, z);
        feet = new Vector3(waypoint.Position.X, height ?? 0f, waypoint.Position.Y);
        return height.HasValue && float.IsFinite(height.Value);
    }

    private bool Clear(Vector3 from, Vector3 to, int layer)
    {
        if (!_allowsSegment(from, to)) return false;
        double dx = Delta(to.X, from.X), dz = Delta(to.Z, from.Z);
        double length = SideLength(dx, dz);
        if (NoSideLines(length)) return true;
        double scale = SideScale(layer, length);
        double ox = SideOffsetX(dz, scale), oz = SideOffsetZ(dx, scale);
        return _allowsSegment(Offset(from, ox, oz), Offset(to, ox, oz)) &&
            _allowsSegment(Offset(from, -ox, -oz), Offset(to, -ox, -oz));
    }

    internal static Vector3 Offset(Vector3 feet, double ox, double oz)
        => new((float)(feet.X + ox), feet.Y, (float)(feet.Z + oz));
}
