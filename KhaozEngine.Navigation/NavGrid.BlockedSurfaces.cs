using System;

namespace KhaozEngine.Navigation;

public sealed partial class NavGrid
{
    /// <summary>
    /// Rebuilds a surface grid from a stored blocked mask and surface heights, so a grid baked through
    /// <see cref="FromSurfaces"/> can be restored without its sampler. <paramref name="blocked"/> and
    /// <paramref name="heights"/> are row-major and hold exactly <c>width * height</c> entries. A blocked
    /// cell is one whose <see cref="ClearanceAt"/> was 0. Clearance is recomputed from the mask by the same
    /// transform a fresh build uses, so every <see cref="ClearanceAt"/> and every
    /// <see cref="SurfaceHeightAt"/> matches the grid the mask came from. Open cells require finite heights.
    /// Heights on blocked cells are copied but never observable, since <see cref="SurfaceHeightAt"/> returns
    /// null there. Both spans are copied. <see cref="HasSurfaceHeights"/> is true. The remaining arguments
    /// mean and validate as in <see cref="FromSurfaces"/>.
    /// </summary>
    public static NavGrid FromBlockedSurfaces(
        int width, int height, float cellSize, float originX, float originZ,
        ReadOnlySpan<bool> blocked, ReadOnlySpan<float> heights,
        float yMin = float.NegativeInfinity, float yMax = float.PositiveInfinity, float yawRadians = 0f)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width), width, "Width must be positive.");
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height), height, "Height must be positive.");
        if (cellSize <= 0f) throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");

        if (!float.IsFinite(yawRadians)) throw new ArgumentOutOfRangeException(nameof(yawRadians));

        long cells = (long)width * height;
        if (blocked.Length != cells)
            throw new ArgumentException($"Blocked mask must hold width * height = {cells} entries, got {blocked.Length}.", nameof(blocked));
        if (heights.Length != cells)
            throw new ArgumentException($"Heights must hold width * height = {cells} entries, got {heights.Length}.", nameof(heights));

        for (int i = 0; i < heights.Length; i++)
        {
            if (!blocked[i] && !float.IsFinite(heights[i]))
                throw new ArgumentOutOfRangeException(nameof(heights), heights[i], $"Open cell {i} needs a finite height.");
        }

        bool[] mask = blocked.ToArray();
        byte[] clearance = ClearanceTransform.Compute(mask, width, height);
        return new NavGrid(clearance, width, height, cellSize, originX, originZ, yMin, yMax, heights.ToArray(), yawRadians);
    }
}
