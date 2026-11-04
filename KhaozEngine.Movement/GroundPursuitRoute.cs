using System;
using System.Numerics;
using KhaozEngine.Navigation;

namespace KhaozEngine.Movement;

/// <summary>A validated walk corridor and its horizontal progress, independent of physics stepping.</summary>
internal sealed class GroundPursuitRoute
{
    private readonly Vector3[] _feet;
    private readonly NavPath _path;
    private readonly NavSpace _space;
    private readonly Vector3 _origin;
    private Vector3 _legOrigin;
    private int _index;

    private GroundPursuitRoute(NavPath path, NavSpace space, Vector3 origin, Vector3[] feet)
    {
        _path = path;
        _space = space;
        _origin = origin;
        _legOrigin = origin;
        _feet = feet;
    }

    internal Vector3 Endpoint => _feet[^1];
    internal Vector3 Active => _feet[_index];

    internal static GroundPursuitDetail Validate(NavPath path, NavSpace space, NavGoalRegion goal,
        Vector3 start, Func<Vector3, Vector3, bool> allowsSegment, out GroundPursuitRoute? route)
    {
        route = null;
        if (path.Status == NavPathStatus.Partial) return GroundPursuitDetail.PartialRoute;
        if (path.Status != NavPathStatus.Complete) return GroundPursuitDetail.UnreachableRoute;
        if (path.Waypoints.Count == 0) return GroundPursuitDetail.EmptyRoute;
        var feet = new Vector3[path.Waypoints.Count];
        for (int i = 0; i < feet.Length; i++)
        {
            NavWaypoint waypoint = path.Waypoints[i];
            if (waypoint.Kind != NavWaypointKind.Walk) return GroundPursuitDetail.NonWalkRoute;
            if ((uint)waypoint.Layer >= (uint)space.Layers.Count ||
                !float.IsFinite(waypoint.Position.X) || !float.IsFinite(waypoint.Position.Y))
                return GroundPursuitDetail.InvalidEndpoint;
            NavGrid grid = space.Layers[waypoint.Layer];
            (int x, int z) = grid.CellOf(waypoint.Position.X, waypoint.Position.Y);
            if (grid.RestingHeightAt(x, z) is not float height || !float.IsFinite(height))
                return GroundPursuitDetail.InvalidEndpoint;
            feet[i] = new(waypoint.Position.X, height, waypoint.Position.Y);
        }
        if (!goal.Contains(feet[^1])) return GroundPursuitDetail.InvalidEndpoint;
        // Validate the physical start and each kept segment, including shortcuts made by the straightener.
        Vector3 from = start;
        foreach (Vector3 to in feet)
        {
            if (!allowsSegment(from, to)) return GroundPursuitDetail.InvalidEntry;
            from = to;
        }
        route = new(path, space, start, feet);
        return GroundPursuitDetail.None;
    }

    // A crossed waypoint may enter the next layer through segment admission. A third layer is off-route.
    // Without crossing, proximity only advances on the active layer and a refused shortcut still permits approach.
    internal bool Advance(Vector3 position, float radius, Func<Vector3, Vector3, bool> allowsSegment)
    {
        int layer = _space.LayerAt(position);
        while (_index < _feet.Length - 1)
        {
            bool passed = PassedActive(position, radius);
            if (layer != _path.Waypoints[_index].Layer)
            {
                if (!passed) break;
                if (layer != _path.Waypoints[_index + 1].Layer) return false;
            }
            if (!passed && Distance(position, Active) > radius) break;
            if (!allowsSegment(position, _feet[_index + 1])) return !passed;
            _index++;
            _legOrigin = position;
        }
        return true;
    }

    private bool PassedActive(Vector3 position, float radius)
    {
        // Crossing follows the actual steering leg. Proximity skipping a waypoint does not visit it.
        Vector3 from = _legOrigin;
        double dx = (double)Active.X - from.X, dz = (double)Active.Z - from.Z;
        double squared = dx * dx + dz * dz;
        double px = (double)position.X - Active.X, pz = (double)position.Z - Active.Z;
        if (squared == 0 || px * dx + pz * dz < 0) return false;
        double lateral = Math.Abs(px * dz - pz * dx) / Math.Sqrt(squared);
        // Match the follower's float-step slack so an on-line controller step survives world-coordinate rounding.
        float magnitude = MathF.Max(MaxAbsXz(from), MathF.Max(MaxAbsXz(Active), MaxAbsXz(position)));
        double ulp = magnitude == float.MaxValue ? (double)magnitude - MathF.BitDecrement(magnitude)
            : (double)MathF.BitIncrement(magnitude) - magnitude;
        return lateral <= radius + 4 * ulp;
    }

    private static float MaxAbsXz(Vector3 position) => MathF.Max(MathF.Abs(position.X), MathF.Abs(position.Z));

    internal bool AtTerminal(Vector3 position, float radius)
        => _index == _feet.Length - 1 && _path.Waypoints[^1].Layer == _space.LayerAt(position)
            && Distance(position, Endpoint) <= radius;

    internal double CorridorDistance(Vector3 position)
    {
        Vector3 from = _index == 0 ? _origin : _feet[_index - 1];
        double dx = (double)Active.X - from.X, dz = (double)Active.Z - from.Z;
        double squared = dx * dx + dz * dz;
        double t = squared == 0 ? 0 : Math.Clamp((((double)position.X - from.X) * dx +
            ((double)position.Z - from.Z) * dz) / squared, 0, 1);
        double x = position.X - (from.X + t * dx), z = position.Z - (from.Z + t * dz);
        return Math.Sqrt(x * x + z * z);
    }

    internal double Remaining(Vector3 position)
    {
        double length = Distance(position, Active);
        for (int i = _index + 1; i < _feet.Length; i++) length += Distance(_feet[i - 1], _feet[i]);
        return length;
    }

    internal bool TryDirection(Vector3 position, out Vector2 direction)
    {
        double dx = (double)Active.X - position.X, dz = (double)Active.Z - position.Z;
        double length = Math.Sqrt(dx * dx + dz * dz);
        direction = length == 0 ? default : new((float)(dx / length), (float)(dz / length));
        return length > 0;
    }

    internal static double Distance(Vector3 from, Vector3 to)
    {
        double dx = (double)to.X - from.X, dz = (double)to.Z - from.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }
}
