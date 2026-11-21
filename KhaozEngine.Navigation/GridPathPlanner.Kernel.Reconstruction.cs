using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    partial struct SearchKernel<TGoal>
    {
        void BeginReconstruction()
        {
            if (!_reachedGoal && _closest == _startId)
            {
                Publish(NavPath.Unreachable);
                return;
            }

            _scratch.Chain.Clear();
            _parent = _closest;
            Phase = SearchPhase.Parent;
        }

        void VisitParent()
        {
            _scratch.Chain.Add(_parent);
            _parent = _scratch.CameFrom(_parent);
            if (_parent != -1) return;

            if (_scratch.Chain.Count > 1)
                Phase = SearchPhase.Reverse;
            else
                BeginDecoding();
        }

        void ReversePair()
        {
            List<int> chain = _scratch.Chain;
            int end = chain.Count - 1 - _waypoint;
            (chain[_waypoint], chain[end]) = (chain[end], chain[_waypoint]);
            if (++_waypoint == chain.Count / 2) BeginDecoding();
        }

        void BeginDecoding()
        {
            if (_planner._traversal is null && _pointGoal is Vector2 point)
            {
                Publish(_planner.PullPointPath(_scratch, _reachedGoal, point, _radius));
                return;
            }

            List<int> chain = _scratch.Chain;
            _scratch.Waypoints.Clear();
            _waypoint = chain.Count > 1 && _scratch.Incoming(chain[1]) is null ? 1 : 0;
            Phase = SearchPhase.Decode;
        }

        void DecodeWaypoint()
        {
            int node = _scratch.Chain[_waypoint];
            (int layer, int x, int z) = _planner.Decode(node);
            NavWaypointKind kind = _waypoint > 0 ? WaypointKind(_scratch.Incoming(node)) : NavWaypointKind.Walk;
            _scratch.Waypoints.Add(new NavWaypoint(_planner._space.Layers[layer].CellCenter(x, z), layer)
            {
                Kind = kind,
            });
            if (++_waypoint == _scratch.Chain.Count) Phase = SearchPhase.Allocate;
        }

        void AllocateResult()
        {
            _resultArray = new NavWaypoint[_scratch.Waypoints.Count];
            _waypoint = 0;
            Phase = SearchPhase.Copy;
        }

        void CopyWaypoint()
        {
            _resultArray![_waypoint] = _scratch.Waypoints[_waypoint];
            if (++_waypoint == _resultArray.Length)
                Publish(new NavPath(_reachedGoal ? NavPathStatus.Complete : NavPathStatus.Partial, _resultArray));
        }
    }
}
