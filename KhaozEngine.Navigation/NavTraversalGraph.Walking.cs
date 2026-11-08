using System.Collections.Generic;

namespace KhaozEngine.Navigation;

public sealed partial class NavTraversalGraph
{
    readonly HashSet<(int FromLayer, int FromX, int FromZ, int ToLayer, int ToX, int ToZ)> _stairEdges = new();

    // Supported segments may use ordinary exits and accepted Stair links, never Hop or Drop.
    internal bool CanWalk(int fromLayer, int fromX, int fromZ, int toLayer, int toX, int toZ)
        => IsNodePassable(fromLayer, fromX, fromZ) && IsNodePassable(toLayer, toX, toZ) &&
            (_stairEdges.Contains((fromLayer, fromX, fromZ, toLayer, toX, toZ)) ||
                fromLayer == toLayer && CanWalkNeighbor(fromLayer, fromX, fromZ, toX, toZ));

    // Neighbor search cannot borrow a special link, even when its endpoints are adjacent.
    internal bool CanWalkNeighbor(int layer, int x, int z, int nx, int nz)
    {
        if (!IsNodePassable(layer, x, z) || !IsNodePassable(layer, nx, nz)) return false;
        for (int i = 0; i < 8; i++)
            if (nx - x == NavTraversalLayer.NeighborX(i) && nz - z == NavTraversalLayer.NeighborZ(i))
                return (Layers[layer].ExitMask(x, z) & (1 << i)) != 0;
        return false;
    }
}
