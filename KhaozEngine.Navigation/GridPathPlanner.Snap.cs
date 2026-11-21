using System;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
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
            _maxRing = planner._traversal is null ? (int)MathF.Ceiling(snapRadius / grid.CellSize) : 0;
            _x = _queryX;
            _z = _queryZ;
            _bestDistance = float.PositiveInfinity;
            Done = _maxRing < 0;
        }

        internal bool Done { get; private set; }
        internal bool SnappedToOwnCell { get; private set; }
        internal Vector2? Result { get; private set; }

        internal void Probe()
        {
            if (_planner._traversal is not null)
            {
                Result = _planner.SnapTraversal(_layer, _queryX, _queryZ, out bool ownCell);
                SnappedToOwnCell = ownCell;
                Done = true;
                return;
            }

            if (_grid.InBounds(_x, _z) && !_planner.Blocks(_layer, _radius, _x, _z))
            {
                Vector2 centre = _grid.CellCenter(_x, _z);
                float distance = Vector2.DistanceSquared(centre, _query);
                if (distance < _bestDistance)
                {
                    _bestDistance = distance;
                    _bestCentre = centre;
                }
            }

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

            if (_bestDistance < float.PositiveInfinity)
            {
                Result = _bestCentre;
                SnappedToOwnCell = _ring == 0;
                Done = true;
                return;
            }

            // Stop at the last representable ring. An inclusive bound of int.MaxValue
            // must end here too, without incrementing a coordinate past it.
            if (_ring == _maxRing || (long)_queryX - _ring - 1 < int.MinValue ||
                (long)_queryX + _ring + 1 > int.MaxValue || (long)_queryZ - _ring - 1 < int.MinValue ||
                (long)_queryZ + _ring + 1 > int.MaxValue)
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
