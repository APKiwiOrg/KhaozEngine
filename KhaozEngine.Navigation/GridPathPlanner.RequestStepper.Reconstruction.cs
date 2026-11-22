using System.Collections.Generic;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    partial struct RequestStepper
    {
        void BeginReconstruction()
        {
            if (!ShouldReconstruct(_reachedGoal, _closest, _startId))
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
            _parent = AppendParent(_scratch, _scratch.Chain, _parent);
            if (_parent != -1) return;

            if (_scratch.Chain.Count > 1)
                Phase = SearchPhase.Reverse;
            else
                BeginDecoding();
        }

        void ReverseChain()
        {
            List<int> chain = _scratch.Chain;
            int end = chain.Count - 1 - _waypoint;
            (chain[_waypoint], chain[end]) = (chain[end], chain[_waypoint]);
            if (++_waypoint == chain.Count / 2) BeginDecoding();
        }

        void BeginDecoding()
        {
            _scratch.Waypoints.Clear();
            _waypoint = FirstRawWaypoint(_scratch, _scratch.Chain);
            Phase = SearchPhase.Decode;
        }

        void DecodeWaypoint()
        {
            _scratch.Waypoints.Add(_planner.DecodeRawWaypoint(_scratch, _scratch.Chain[_waypoint], _waypoint));
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
            NavWaypoint[] result = _resultArray!;
            result[_waypoint] = _scratch.Waypoints[_waypoint];
            if (++_waypoint == result.Length) Publish(new NavPath(PathStatus(_reachedGoal), result));
        }
    }
}
