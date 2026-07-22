using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Navigation;

/// <summary>
/// A directed connection between a cell in one <see cref="NavGrid"/> layer and a cell in another
/// (or the same) layer, representing a stair, ladder, or other vertical transition a path can cross.
/// Directed: a two-way stair is modeled as two <see cref="NavLink"/> instances, one per direction.
/// Layer indices and cell coordinates are validated against the owning <see cref="NavSpace"/>'s
/// layers when the link is added.
/// </summary>
/// <param name="FromLayer">Index into <see cref="NavSpace.Layers"/> the link starts in.</param>
/// <param name="FromX">Grid cell X coordinate the link starts at, in <see cref="FromLayer"/>.</param>
/// <param name="FromZ">Grid cell Z coordinate the link starts at, in <see cref="FromLayer"/>.</param>
/// <param name="ToLayer">Index into <see cref="NavSpace.Layers"/> the link ends in.</param>
/// <param name="ToX">Grid cell X coordinate the link ends at, in <see cref="ToLayer"/>.</param>
/// <param name="ToZ">Grid cell Z coordinate the link ends at, in <see cref="ToLayer"/>.</param>
public readonly record struct NavLink(int FromLayer, int FromX, int FromZ, int ToLayer, int ToX, int ToZ)
{
    /// <summary>What kind of transition this link is (default <see cref="NavLinkKind.Stair"/>). A
    /// <see cref="NavLinkKind.Hop"/> link is charged the planner's hop cost and surfaces the follower's
    /// hop seam. Existing six-argument constructions default to <see cref="NavLinkKind.Stair"/>, so shipped
    /// links (the dungeon stair connections) are unchanged.</summary>
    public NavLinkKind Kind { get; init; } = NavLinkKind.Stair;
}

/// <summary>
/// A multi-layer navigable space: a stack of <see cref="NavGrid"/> layers (each covering its own
/// vertical band, see <see cref="NavGrid.ContainsY"/>) joined by directed <see cref="NavLink"/> stair
/// connections. Consumed by the path planner, which walks within a layer via grid adjacency and
/// crosses layers only via a link. Immutable once constructed. Render-free, deterministic.
/// </summary>
public sealed class NavSpace
{
    /// <summary>The layers making up this space, in the order passed to the constructor. At least one.</summary>
    public IReadOnlyList<NavGrid> Layers { get; }

    /// <summary>The directed stair connections between layers. Empty when none were supplied.</summary>
    public IReadOnlyList<NavLink> Links { get; }

    /// <summary>
    /// Builds a space from <paramref name="layers"/> and, optionally, <paramref name="links"/> between
    /// them. Requires at least one layer, and every link endpoint (both its layer index and its cell
    /// coordinates within that layer) must be in bounds, else throws <see cref="ArgumentException"/>.
    /// </summary>
    public NavSpace(IReadOnlyList<NavGrid> layers, IReadOnlyList<NavLink>? links = null)
    {
        if (layers is null) throw new ArgumentNullException(nameof(layers));
        if (layers.Count == 0) throw new ArgumentException("NavSpace requires at least one layer.", nameof(layers));

        links ??= Array.Empty<NavLink>();
        for (int i = 0; i < links.Count; i++)
        {
            NavLink link = links[i];
            ValidateEndpoint(layers, link.FromLayer, link.FromX, link.FromZ, i, isFrom: true, nameof(links));
            ValidateEndpoint(layers, link.ToLayer, link.ToX, link.ToZ, i, isFrom: false, nameof(links));
        }

        Layers = layers;
        Links = links;
    }

    static void ValidateEndpoint(IReadOnlyList<NavGrid> layers, int layer, int x, int z, int linkIndex, bool isFrom, string paramName)
    {
        string endpoint = isFrom ? "From" : "To";
        if (layer < 0 || layer >= layers.Count)
            throw new ArgumentException(
                $"Link {linkIndex}: {endpoint}Layer {layer} is out of range for {layers.Count} layer(s).",
                paramName);

        if (!layers[layer].InBounds(x, z))
            throw new ArgumentException(
                $"Link {linkIndex}: {endpoint} cell ({x}, {z}) is out of bounds for layer {layer}.",
                paramName);
    }

    /// <summary>Builds a single-layer space wrapping <paramref name="grid"/>, with no links.</summary>
    public static NavSpace Single(NavGrid grid) => new(new[] { grid });

    /// <summary>
    /// Resolves the layer index a world Y coordinate belongs to. With a single layer, always 0.
    /// Otherwise the lowest-index layer whose <see cref="NavGrid.ContainsY"/> is true wins. If no
    /// layer contains <paramref name="y"/>, returns the layer minimizing the distance from
    /// <paramref name="y"/> to its band center (YMin + YMax) * 0.5, considering only layers with a
    /// finite band, ties going to the lowest index. If every layer has an infinite band, returns 0.
    /// </summary>
    public int LayerOf(float y)
    {
        if (Layers.Count == 1) return 0;

        for (int i = 0; i < Layers.Count; i++)
            if (Layers[i].ContainsY(y)) return i;

        int bestIndex = -1;
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < Layers.Count; i++)
        {
            NavGrid layer = Layers[i];
            if (float.IsInfinity(layer.YMin) || float.IsInfinity(layer.YMax)) continue;

            float center = (layer.YMin + layer.YMax) * 0.5f;
            float distance = Math.Abs(y - center);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex >= 0 ? bestIndex : 0;
    }

    /// <summary>
    /// Resolves the layer a world position belongs to, surface-aware. With a single layer, always 0.
    /// Otherwise, among the layers that carry surface heights (<see cref="NavGrid.HasSurfaceHeights"/>)
    /// and have a passable surface at the position's cell, the layer whose surface Y is nearest to
    /// <paramref name="position"/>.Y wins (ties to the lowest index), so an agent standing on a bridge
    /// deck resolves to the deck layer even when its Y also falls inside the ground layer's band.
    /// When no layer has a surface there (or none carries heights, e.g. the dungeon adapter's
    /// <see cref="NavGrid.FromWalkable"/> grids), falls back to <see cref="LayerOf"/> on the Y band,
    /// so every pre-layered space resolves exactly as before.
    /// </summary>
    public int LayerAt(Vector3 position)
    {
        if (Layers.Count == 1) return 0;

        int bestIndex = -1;
        float bestDistance = float.PositiveInfinity;
        for (int i = 0; i < Layers.Count; i++)
        {
            NavGrid layer = Layers[i];
            if (!layer.HasSurfaceHeights) continue;

            (int cx, int cz) = layer.CellOf(position.X, position.Z);
            float? surface = layer.SurfaceHeightAt(cx, cz);
            if (surface is null) continue;

            float distance = Math.Abs(position.Y - surface.Value);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = i;
            }
        }

        return bestIndex >= 0 ? bestIndex : LayerOf(position.Y);
    }
}
