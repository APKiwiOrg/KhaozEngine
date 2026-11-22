using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
    /// <summary>Owned immutable snapshot of the supplied immutable grids, in constructor order. At least one.</summary>
    public IReadOnlyList<NavGrid> Layers { get; }

    /// <summary>Owned immutable snapshot of directed links, in constructor order. Empty when none were supplied.</summary>
    public IReadOnlyList<NavLink> Links { get; }

    /// <summary>
    /// Builds a space from <paramref name="layers"/> and, optionally, <paramref name="links"/> between
    /// them. Copies both containers into immutable snapshots while sharing the immutable grids.
    /// Neither collection interfaces nor SyncRoot expose mutable backing storage.
    /// Requires at least one layer, and every link endpoint (both its layer index and its cell
    /// coordinates within that layer) must be in bounds, else throws <see cref="ArgumentException"/>.
    /// </summary>
    public NavSpace(IReadOnlyList<NavGrid> layers, IReadOnlyList<NavLink>? links = null)
    {
        if (layers is null) throw new ArgumentNullException(nameof(layers));
        if (layers.Count == 0) throw new ArgumentException("NavSpace requires at least one layer.", nameof(layers));

        // Copy first, even for immutable inputs, so their backing storage is never retained.
        Layers = ImmutableArray.CreateRange(CopyToArray(layers));
        Links = ImmutableArray.CreateRange(CopyToArray(links ?? Array.Empty<NavLink>()));
        for (int i = 0; i < Links.Count; i++)
        {
            NavLink link = Links[i];
            ValidateEndpoint(Layers, link.FromLayer, link.FromX, link.FromZ, i, isFrom: true, nameof(links));
            ValidateEndpoint(Layers, link.ToLayer, link.ToX, link.ToZ, i, isFrom: false, nameof(links));
        }
    }

    static T[] CopyToArray<T>(IReadOnlyList<T> values)
    {
        var copy = new T[values.Count];
        for (int i = 0; i < copy.Length; i++) copy[i] = values[i];
        return copy;
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
            CompareBandCenter(Layers[i], y, i, ref bestIndex, ref bestDistance);
        return bestIndex >= 0 ? bestIndex : 0;
    }

    /// <summary>
    /// Resolves the layer a world position belongs to, using physical feet heights. With a single layer, always 0.
    /// Otherwise, among the layers that carry surface heights and have an open cell at
    /// the position, the layer whose <see cref="NavGrid.RestingHeightAt"/> is nearest to
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
            CompareSurface(Layers[i], position, i, ref bestIndex, ref bestDistance);
        return bestIndex >= 0 ? bestIndex : LayerOf(position.Y);
    }

    static void CompareSurface(NavGrid grid, Vector3 position, int index, ref int bestIndex, ref float bestDistance)
    {
        if (!grid.HasSurfaceHeights) return;
        (int x, int z) = grid.CellOf(position.X, position.Z);
        float? resting = grid.RestingHeightAt(x, z);
        if (resting is null) return;
        CompareLayer(position.Y, resting.Value, index, ref bestIndex, ref bestDistance);
    }

    static void CompareBandCenter(NavGrid grid, float y, int index, ref int bestIndex, ref float bestDistance)
    {
        if (!float.IsInfinity(grid.YMin) && !float.IsInfinity(grid.YMax))
            CompareLayer(y, (grid.YMin + grid.YMax) * 0.5f, index, ref bestIndex, ref bestDistance);
    }

    static void CompareLayer(float y, float candidateHeight, int index, ref int bestIndex, ref float bestDistance)
    {
        float distance = Math.Abs(y - candidateHeight);
        if (distance < bestDistance)
        {
            bestDistance = distance;
            bestIndex = index;
        }
    }

    /// <summary>Resolves a position in constructor order, one surface or band probe at a time.</summary>
    internal struct LayerCursor(NavSpace space, Vector3 position)
    {
        enum ProbeKind { Surface, ContainsY, BandCenter }

        ProbeKind _kind;
        int _index;
        int _best;
        float _distance = float.PositiveInfinity;

        internal bool Done { get; private set; } = space.Layers.Count == 1;

        internal int Layer => _best;

        internal void Probe()
        {
            NavGrid grid = space.Layers[_index];
            switch (_kind)
            {
                case ProbeKind.Surface:
                    CompareSurface(grid, position, _index, ref _best, ref _distance);
                    break;
                case ProbeKind.ContainsY:
                    if (grid.ContainsY(position.Y))
                    {
                        _best = _index;
                        Done = true;
                        return;
                    }
                    break;
                case ProbeKind.BandCenter:
                    CompareBandCenter(grid, position.Y, _index, ref _best, ref _distance);
                    break;
            }

            if (++_index < space.Layers.Count) return;
            if (_kind == ProbeKind.BandCenter || _distance < float.PositiveInfinity)
            {
                Done = true;
                return;
            }
            _index = 0;
            _kind++;
        }

    }
}
