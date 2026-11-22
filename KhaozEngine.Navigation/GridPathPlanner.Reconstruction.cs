using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    /// <summary>Rebuilds the chosen incoming edges. Guarded and region queries retain cell centers.</summary>
    NavPath Reconstruct(SearchScratch scratch, int target, bool reachedGoal, Vector2? goalPoint, float agentRadius)
    {
        List<int> chain = scratch.Chain;
        chain.Clear();
        for (int node = target; node != -1;)
            node = AppendParent(scratch, chain, node);
        chain.Reverse();
        if (_traversal is null && goalPoint is Vector2 point) return PullPointPath(scratch, reachedGoal, point, agentRadius);

        List<NavWaypoint> waypoints = scratch.Waypoints;
        waypoints.Clear();
        int first = FirstRawWaypoint(scratch, chain);
        for (int i = first; i < chain.Count; i++)
            waypoints.Add(DecodeRawWaypoint(scratch, chain[i], i));
        return new NavPath(PathStatus(reachedGoal), waypoints.ToArray());
    }

    static int AppendParent(SearchScratch scratch, List<int> chain, int node)
    {
        chain.Add(node);
        return scratch.CameFrom(node);
    }

    // A link on the first edge keeps its source as well as the landing, including a first-edge Drop.
    static int FirstRawWaypoint(SearchScratch scratch, List<int> chain)
        => chain.Count > 1 && scratch.Incoming(chain[1]) is null ? 1 : 0;

    NavWaypoint DecodeRawWaypoint(SearchScratch scratch, int node, int index)
    {
        (int layer, int x, int z) = Decode(node);
        return WaypointFromIncoming(_space.Layers[layer].CellCenter(x, z), layer, index > 0 ? scratch.Incoming(node) : null);
    }

    static NavWaypoint WaypointFromIncoming(Vector2 position, int layer, NavLinkKind? incoming)
        => new(position, layer) { Kind = WaypointKind(incoming) };

    static NavPathStatus PathStatus(bool reachedGoal) => reachedGoal ? NavPathStatus.Complete : NavPathStatus.Partial;
}
