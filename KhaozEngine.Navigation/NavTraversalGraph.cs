using System;
using System.Collections.Generic;

namespace KhaozEngine.Navigation;

/// <summary>
/// Immutable node and edge decisions baked for one agent profile. No live world queries are retained.
/// Grid clearance is checked separately by the planner at radius zero, since the profile owns clearance.
/// </summary>
public sealed class NavTraversalGraph
{
    readonly HashSet<(int FromLayer, int FromX, int FromZ, int ToLayer, int ToX, int ToZ)> _linkEdges = new();

    /// <summary>Radius for which the traversal decisions were baked.</summary>
    public float AgentRadius { get; }

    /// <summary>Height for which the traversal decisions were baked.</summary>
    public float AgentHeight { get; }

    /// <summary>
    /// Owned snapshot of the source space with read-only layer and original link containers.
    /// Its immutable grids are shared. Only <see cref="Links"/> authorize link traversal.
    /// </summary>
    public NavSpace Space { get; }

    /// <summary>Owned read-only container of immutable traversal layers.</summary>
    public IReadOnlyList<NavTraversalLayer> Layers { get; }

    /// <summary>Owned read-only accepted subset of the source space's links, in supplied order.</summary>
    public IReadOnlyList<NavLink> Links { get; }

    /// <summary>
    /// Copies the input containers and validates dimensions, directed exits and accepted link endpoints.
    /// Radius must be finite and nonnegative. Height must be finite and positive.
    /// </summary>
    public NavTraversalGraph(NavSpace space, float agentRadius, float agentHeight,
        IReadOnlyList<NavTraversalLayer> layers, IReadOnlyList<NavLink> links)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(layers);
        ArgumentNullException.ThrowIfNull(links);
        if (!float.IsFinite(agentRadius) || agentRadius < 0f)
            throw new ArgumentOutOfRangeException(nameof(agentRadius));
        if (!float.IsFinite(agentHeight) || agentHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(agentHeight));

        NavGrid[] grids = Copy(space.Layers);
        foreach (NavGrid grid in grids)
            if (grid is null) throw new ArgumentException("Source layers must not be null.", nameof(space));
        Space = new NavSpace(Array.AsReadOnly(grids), Array.AsReadOnly(Copy(space.Links)));
        AgentRadius = agentRadius;
        AgentHeight = agentHeight;
        Layers = Array.AsReadOnly(Copy(layers));
        Links = Array.AsReadOnly(Copy(links));

        if (Layers.Count != grids.Length)
            throw new ArgumentException("Traversal layers must match the source space.", nameof(layers));
        for (int i = 0; i < Layers.Count; i++)
        {
            NavTraversalLayer layer = Layers[i];
            if (layer is null || layer.Width != grids[i].Width || layer.Height != grids[i].Height)
                throw new ArgumentException("Traversal layer dimensions must match the source grid.", nameof(layers));
            ValidateExits(layer);
        }

        var sourceLinks = new HashSet<NavLink>(Space.Links);
        foreach (NavLink link in Links)
        {
            if (!IsNodePassable(link.FromLayer, link.FromX, link.FromZ) ||
                !IsNodePassable(link.ToLayer, link.ToX, link.ToZ) || !sourceLinks.Contains(link))
                throw new ArgumentException("Accepted links must join accepted nodes and belong to the source space.", nameof(links));
            _linkEdges.Add((link.FromLayer, link.FromX, link.FromZ, link.ToLayer, link.ToX, link.ToZ));
        }
    }

    /// <summary>Whether the baked profile accepted this node. False outside the graph.</summary>
    public bool IsNodePassable(int layer, int x, int z)
        => layer >= 0 && layer < Layers.Count && Layers[layer].IsAccepted(x, z);

    /// <summary>Whether a directed neighbor edge or accepted link joins these accepted nodes.</summary>
    public bool CanTraverse(int fromLayer, int fromX, int fromZ, int toLayer, int toX, int toZ)
    {
        if (!IsNodePassable(fromLayer, fromX, fromZ) || !IsNodePassable(toLayer, toX, toZ)) return false;
        if (_linkEdges.Contains((fromLayer, fromX, fromZ, toLayer, toX, toZ))) return true;
        if (fromLayer != toLayer) return false;
        int dx = toX - fromX;
        int dz = toZ - fromZ;
        byte exits = Layers[fromLayer].ExitMask(fromX, fromZ);
        for (int i = 0; i < 8; i++)
            if (dx == NavTraversalLayer.NeighborX(i) && dz == NavTraversalLayer.NeighborZ(i))
                return (exits & (1 << i)) != 0;
        return false;
    }

    static void ValidateExits(NavTraversalLayer layer)
    {
        for (int z = 0; z < layer.Height; z++)
            for (int x = 0; x < layer.Width; x++)
            {
                byte exits = layer.ExitMask(x, z);
                if (exits != 0 && !layer.IsAccepted(x, z))
                    throw new ArgumentException("Rejected nodes must have no exits.", "layers");
                for (int i = 0; i < 8; i++)
                    if ((exits & (1 << i)) != 0 &&
                        !layer.IsAccepted(x + NavTraversalLayer.NeighborX(i), z + NavTraversalLayer.NeighborZ(i)))
                        throw new ArgumentException("Exits must target accepted nodes within the layer.", "layers");
            }
    }

    static T[] Copy<T>(IReadOnlyList<T> values)
    {
        var copy = new T[values.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = values[i];
        return copy;
    }
}
