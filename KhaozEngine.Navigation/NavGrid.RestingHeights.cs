using System;

namespace KhaozEngine.Navigation;

public sealed partial class NavGrid
{
    readonly float[]? _restingHeights;

    /// <summary>True when this grid carries per-cell physical feet heights supplied through
    /// <see cref="WithRestingHeights"/>.</summary>
    public bool HasRestingHeights => _restingHeights is not null;

    /// <summary>
    /// Physical feet height at (<paramref name="cx"/>, <paramref name="cz"/>) in world Y, or null when
    /// the cell is blocked or out of bounds. When no resting heights are set, returns
    /// <see cref="SurfaceHeightAt"/>, including null for a grid without surface heights.
    /// </summary>
    public float? RestingHeightAt(int cx, int cz)
    {
        if (_restingHeights is null) return SurfaceHeightAt(cx, cz);
        if (!InBounds(cx, cz) || ClearanceAt(cx, cz) == 0) return null;
        return _restingHeights[cz * Width + cx];
    }

    /// <summary>
    /// Returns a new grid with the supplied physical feet heights, keeping all topology, raw surface
    /// heights, clearance, bounds and transform fields unchanged. <paramref name="restingHeights"/> is
    /// copied and must hold exactly <c>Width * Height</c> row-major entries, finite on every open cell.
    /// Values on blocked cells are copied but never observable. The source grid is unchanged.
    /// </summary>
    /// <exception cref="ArgumentException">The array is null or has the wrong cell count.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An open cell has a non-finite height.</exception>
    public NavGrid WithRestingHeights(float[] restingHeights)
    {
        long cells = (long)Width * Height;
        if (restingHeights is null || restingHeights.Length != cells)
            throw new ArgumentException($"Resting heights must hold Width * Height = {cells} entries.", nameof(restingHeights));

        float[] copy = (float[])restingHeights.Clone();
        for (int i = 0; i < copy.Length; i++)
        {
            if (_clearance[i] != 0 && !float.IsFinite(copy[i]))
                throw new ArgumentOutOfRangeException(nameof(restingHeights), copy[i], $"Open cell {i} needs a finite resting height.");
        }

        return new NavGrid(_clearance, Width, Height, CellSize, OriginX, OriginZ, YMin, YMax, _heights, YawRadians, copy);
    }
}
