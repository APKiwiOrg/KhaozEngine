// Frozen copy of the v20.24.0 GridPathPlanner algorithm (KhaozEngine#1288). The equality corpus compares the
// production planner against it, so this file must never change with the production planner.
using System;
using System.Collections.Generic;
using System.Numerics;

using KhaozEngine.Navigation;

namespace KhaozEngine.Tests.Navigation;

internal sealed partial class ReferenceGridPathPlanner
{
    readonly NavTraversalGraph? _traversal;

    /// <summary>
    /// Searches the profile's owned space snapshot using accepted nodes, directed exits and links.
    /// Queries must use the exact baked radius. Clearance is not eroded again. Returned waypoints
    /// remain on validated cell centers, including the goal, without shortcuts or string pulling.
    /// Both endpoints must already occupy admitted raw-passable cells. Snap radius cannot authorize
    /// crossing other nodes to substitute an accepted cell for a rejected endpoint.
    /// The supplied space must contain the same immutable grids and original links in the same order.
    /// </summary>
    public ReferenceGridPathPlanner(NavSpace space, NavTraversalGraph traversal, float hopCostCells = 4f)
        : this(ValidateTraversalSpace(space, traversal), hopCostCells, traversal)
    {
    }

    static NavSpace ValidateTraversalSpace(NavSpace space, NavTraversalGraph traversal)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(traversal);
        NavSpace snapshot = traversal.Space;
        if (space.Layers.Count != snapshot.Layers.Count || space.Links.Count != snapshot.Links.Count)
            throw new ArgumentException("Space must match the traversal graph's source topology.", nameof(space));
        for (int i = 0; i < space.Layers.Count; i++)
            if (!ReferenceEquals(space.Layers[i], snapshot.Layers[i]))
                throw new ArgumentException("Space must use the traversal graph's source grids.", nameof(space));
        for (int i = 0; i < space.Links.Count; i++)
            if (space.Links[i] != snapshot.Links[i])
                throw new ArgumentException("Space must match the traversal graph's source links.", nameof(space));
        return snapshot;
    }

    void ValidateTraversalRadius(float agentRadius)
    {
        if (_traversal is not null && agentRadius != _traversal.AgentRadius)
            throw new ArgumentException("Query radius must equal the traversal profile radius.", nameof(agentRadius));
    }

    bool Blocks(int layer, float agentRadius, int x, int z)
        => _traversal is null
            ? Blocks(_space.Layers[layer], agentRadius, x, z)
            : Blocks(_space.Layers[layer], 0f, x, z) || !_traversal.IsNodePassable(layer, x, z);

    bool CanTraverse(int fromLayer, int fromX, int fromZ, int toLayer, int toX, int toZ)
        => _traversal is null || _traversal.CanTraverse(fromLayer, fromX, fromZ, toLayer, toX, toZ);

    Vector2? SnapTraversal(int layer, int x, int z, out bool snappedToOwnCell)
    {
        snappedToOwnCell = !Blocks(layer, 0f, x, z);
        return snappedToOwnCell ? _space.Layers[layer].CellCenter(x, z) : null;
    }

    NavPath ReconstructTraversal(List<int> chain, bool reachedGoal)
    {
        var waypoints = new List<NavWaypoint>();
        int first = chain.Count > 1 && IsGridStep(Decode(chain[0]), Decode(chain[1])) &&
            !_hopEdges.Contains((chain[0], chain[1])) ? 1 : 0;
        for (int i = first; i < chain.Count; i++)
        {
            (int layer, int x, int z) = Decode(chain[i]);
            bool hop = i > 0 && _hopEdges.Contains((chain[i - 1], chain[i]));
            waypoints.Add(new NavWaypoint(_space.Layers[layer].CellCenter(x, z), layer)
            {
                Kind = hop ? NavWaypointKind.Hop : NavWaypointKind.Walk,
            });
        }
        return new NavPath(reachedGoal ? NavPathStatus.Complete : NavPathStatus.Partial, waypoints);
    }
}
