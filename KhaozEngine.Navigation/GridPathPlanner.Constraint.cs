using System;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    private readonly Func<NavLink, bool>? _allowsTraversal;

    // Movement supplies an immutable captured-footprint restriction over the shared baked graph.
    // Raw grid planning keeps its original line-of-sight and string-pull rules.
    internal GridPathPlanner(NavSpace space, NavTraversalGraph traversal, Func<NavLink, bool> allowsTraversal)
        : this(space, traversal)
    {
        _allowsTraversal = allowsTraversal ?? throw new ArgumentNullException(nameof(allowsTraversal));
    }

    private bool AllowsNode(int layer, int x, int z)
        => _allowsTraversal is null || _allowsTraversal(new(layer, x, z, layer, x, z));

    private bool AllowsEdge(int fromLayer, int x, int z, int toLayer, int tx, int tz)
        => _allowsTraversal is null || _allowsTraversal(new(fromLayer, x, z, toLayer, tx, tz));

    private bool CanWalkNeighbor(int layer, int x, int z, int nx, int nz)
        => (_traversal?.CanWalkNeighbor(layer, x, z, nx, nz) ?? true) && AllowsEdge(layer, x, z, layer, nx, nz);
}
