using System;
using System.Numerics;

namespace KhaozEngine.Navigation;

public sealed partial class GridPathPlanner
{
    private readonly Func<Vector3, Vector3, bool>? _allowsTraversalSegment;

    // Movement supplies an immutable captured-footprint restriction over the shared baked graph.
    // Raw grid planning keeps its original line-of-sight and string-pull rules.
    internal GridPathPlanner(NavSpace space, NavTraversalGraph traversal, Func<Vector3, Vector3, bool> allowsSegment)
        : this(space, traversal)
    {
        _allowsTraversalSegment = allowsSegment ?? throw new ArgumentNullException(nameof(allowsSegment));
    }

    private bool AllowsNode(int layer, int x, int z)
    {
        if (_allowsTraversalSegment is null) return true;
        Vector3 feet = CellFeet(layer, x, z);
        return _allowsTraversalSegment(feet, feet);
    }

    private bool AllowsEdge(int fromLayer, int x, int z, int toLayer, int tx, int tz)
        => _allowsTraversalSegment is null ||
            _allowsTraversalSegment(CellFeet(fromLayer, x, z), CellFeet(toLayer, tx, tz));

    private bool CanWalkNeighbor(int layer, int x, int z, int nx, int nz)
        => (_traversal?.CanWalkNeighbor(layer, x, z, nx, nz) ?? true) && AllowsEdge(layer, x, z, layer, nx, nz);
}
