using System;

namespace KhaozEngine.Navigation;

public static partial class NavLayerBaker
{
    /// <summary>
    /// Bakes standable columns into layers with walked <see cref="NavLinkKind.Stair"/> seams only.
    /// Uses the same column capture, headroom filtering and layer extraction as
    /// <see cref="BakeOverworldLayered"/>, without generating same-layer or cross-layer hops.
    /// Bounds and sizes must be finite. The rectangular XZ region is half-open and each axis uses
    /// the ceiling of its extent divided by <paramref name="cellSize"/>.
    /// <paramref name="extraBlocked"/> excludes a whole column at its cell center.
    /// <paramref name="maxLayerCells"/> bounds the total cells across all layers before dense layer
    /// fields or grids are allocated. An empty world returns one blocked layer, counted in the budget.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="columns"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A bound or size is non-finite, the rectangle is
    /// empty, cell size is non-positive, a height is negative, a budget is non-positive, dimensions
    /// overflow, or the layer-cell count exceeds <paramref name="maxLayerCells"/>.</exception>
    /// <exception cref="InvalidOperationException">The provider returns a count outside its buffer
    /// or standable surfaces that are not strictly ascending.</exception>
    public static NavSpace BakeGroundedLayered(
        INavColumnProvider columns,
        float minX, float minZ, float maxX, float maxZ,
        float cellSize, float stepHeight, float agentHeight,
        int maxSurfacesPerColumn = 4,
        Func<float, float, bool>? extraBlocked = null,
        int maxLayerCells = int.MaxValue)
    {
        if (columns is null) throw new ArgumentNullException(nameof(columns));
        if (!float.IsFinite(minX)) throw new ArgumentOutOfRangeException(nameof(minX));
        if (!float.IsFinite(minZ)) throw new ArgumentOutOfRangeException(nameof(minZ));
        if (!float.IsFinite(maxX) || maxX <= minX) throw new ArgumentOutOfRangeException(nameof(maxX));
        if (!float.IsFinite(maxZ) || maxZ <= minZ) throw new ArgumentOutOfRangeException(nameof(maxZ));
        if (!float.IsFinite(cellSize) || cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize));
        if (!float.IsFinite(stepHeight) || stepHeight < 0f) throw new ArgumentOutOfRangeException(nameof(stepHeight));
        if (!float.IsFinite(agentHeight) || agentHeight < 0f) throw new ArgumentOutOfRangeException(nameof(agentHeight));
        if (maxSurfacesPerColumn < 1) throw new ArgumentOutOfRangeException(nameof(maxSurfacesPerColumn));
        if (maxLayerCells < 1) throw new ArgumentOutOfRangeException(nameof(maxLayerCells));

        int width;
        int height;
        int cellCount;
        try
        {
            width = checked((int)MathF.Ceiling((maxX - minX) / cellSize));
            height = checked((int)MathF.Ceiling((maxZ - minZ) / cellSize));
            cellCount = checked(width * height);
            _ = checked(cellCount + 1);
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Grid dimensions exceed the supported cell count.");
        }
        if (width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Grid dimensions must be positive.");
        if (cellCount > maxLayerCells)
            throw new ArgumentOutOfRangeException(nameof(maxLayerCells), maxLayerCells, "The grid exceeds the layer-cell budget.");

        NavGrid[] grids = BakeLayers(
            columns, width, height, cellSize, minX, minZ, stepHeight, agentHeight,
            maxSurfacesPerColumn, extraBlocked, out _, maxLayerCells);
        return new NavSpace(grids, NavLayerLinks.GenerateGrounded(grids, stepHeight));
    }
}
