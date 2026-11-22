using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    static int SnapRingLimit(NavGrid grid, float snapRadius) => (int)MathF.Ceiling(snapRadius / grid.CellSize);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool SnapRingIsRepresentable(int queryX, int queryZ, int ring)
    {
        int low = int.MinValue + ring, high = int.MaxValue - ring;
        return queryX >= low && queryX <= high && queryZ >= low && queryZ <= high;
    }

    // A positive inclusive bound cannot terminate the legacy ring loop. Even a winner on that ring
    // is refused. The stepper charges the representable ring's candidates before it checks the result.
    static bool CanSelectSnapRing(int right, int bottom) => right != int.MaxValue && bottom != int.MaxValue;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    bool TryImproveSnap(NavGrid grid, int layer, float radius, Vector2 query, int x, int z,
        ref float bestDistance, ref Vector2 bestCenter)
    {
        if (!grid.InBounds(x, z) || Blocks(layer, radius, x, z)) return false;
        Vector2 center = grid.CellCenter(x, z);
        float distance = Vector2.DistanceSquared(center, query);
        if (distance < bestDistance)
        {
            bestDistance = distance;
            bestCenter = center;
            return true;
        }
        return false;
    }

    /// <summary>Visits the same Chebyshev ring candidates in z then x order as synchronous snapping.
    /// Guarded planners probe only the query cell. The first ring with a passable centre wins.</summary>
    struct SnapCursor
    {
        readonly GridPathPlanner _planner;
        readonly NavGrid _grid;
        readonly Vector2 _query;
        readonly int _layer, _queryX, _queryZ, _maxRing;
        readonly float _radius;
        int _ring, _x, _z;
        float _bestDistance;
        Vector2 _bestCentre;

        internal SnapCursor(GridPathPlanner planner, int layer, NavGrid grid, Vector2 query, float radius, float snapRadius)
        {
            _planner = planner;
            _layer = layer;
            _grid = grid;
            _query = query;
            _radius = radius;
            (_queryX, _queryZ) = grid.CellOf(query.X, query.Y);
            _maxRing = planner._traversal is null ? SnapRingLimit(grid, snapRadius) : 0;
            _x = _queryX;
            _z = _queryZ;
            _bestDistance = float.PositiveInfinity;
            Done = _maxRing < 0;
        }

        internal bool Done { get; private set; }
        internal Vector2? Result { get; private set; }

        internal void Probe()
        {
            if (_planner._traversal is not null)
            {
                Result = _planner.SnapTraversal(_layer, _queryX, _queryZ, out _);
                Done = true;
                return;
            }

            _planner.TryImproveSnap(_grid, _layer, _radius, _query, _x, _z, ref _bestDistance, ref _bestCentre);

            int right = _queryX + _ring, bottom = _queryZ + _ring;
            if (_x < right)
            {
                // Only the left and right cells of an interior row lie on this ring.
                _x = _z == _queryZ - _ring || _z == bottom ? _x + 1 : right;
                return;
            }

            if (_z < bottom)
            {
                _z++;
                _x = _queryX - _ring;
                return;
            }

            if (!CanSelectSnapRing(right, bottom))
            {
                Done = true;
                return;
            }

            if (_bestDistance < float.PositiveInfinity)
            {
                Result = _bestCentre;
                Done = true;
                return;
            }

            if (_ring == _maxRing || !SnapRingIsRepresentable(_queryX, _queryZ, _ring + 1))
            {
                Done = true;
                return;
            }
            _ring++;
            _x = _queryX - _ring;
            _z = _queryZ - _ring;
        }
    }
}
