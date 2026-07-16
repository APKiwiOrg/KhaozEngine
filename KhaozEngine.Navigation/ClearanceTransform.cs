using System;

namespace KhaozEngine.Navigation;

/// <summary>
/// Two-pass 2-3 chamfer distance transform. Converts a blocked mask into per-cell clearance,
/// the approximate distance from each cell center to the nearest blocked cell center, in
/// half-cell units (an orthogonal neighbor step costs 2, a diagonal step 3), saturated at 255.
/// Cells outside the grid count as blocked, so clearance falls off toward the borders.
/// Deterministic: fixed scan order, integer math.
/// </summary>
internal static class ClearanceTransform
{
    internal const int OrthogonalCost = 2;
    internal const int DiagonalCost = 3;

    internal static byte[] Compute(bool[] blocked, int width, int height)
    {
        if (blocked is null) throw new ArgumentNullException(nameof(blocked));
        if (width <= 0 || height <= 0 || blocked.Length != width * height)
            throw new ArgumentException("Mask dimensions must be positive and match the array length.");

        var dist = new int[width * height];
        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = z * width + x;
                if (blocked[i]) { dist[i] = 0; continue; }
                int edge = Math.Min(Math.Min(x, z), Math.Min(width - 1 - x, height - 1 - z)) + 1;
                dist[i] = Math.Min(255, edge * OrthogonalCost);
            }
        }

        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                int i = z * width + x;
                int d = dist[i];
                if (x > 0) d = Math.Min(d, dist[i - 1] + OrthogonalCost);
                if (z > 0) d = Math.Min(d, dist[i - width] + OrthogonalCost);
                if (x > 0 && z > 0) d = Math.Min(d, dist[i - width - 1] + DiagonalCost);
                if (x < width - 1 && z > 0) d = Math.Min(d, dist[i - width + 1] + DiagonalCost);
                dist[i] = d;
            }
        }

        for (int z = height - 1; z >= 0; z--)
        {
            for (int x = width - 1; x >= 0; x--)
            {
                int i = z * width + x;
                int d = dist[i];
                if (x < width - 1) d = Math.Min(d, dist[i + 1] + OrthogonalCost);
                if (z < height - 1) d = Math.Min(d, dist[i + width] + OrthogonalCost);
                if (x < width - 1 && z < height - 1) d = Math.Min(d, dist[i + width + 1] + DiagonalCost);
                if (x > 0 && z < height - 1) d = Math.Min(d, dist[i + width - 1] + DiagonalCost);
                dist[i] = d;
            }
        }

        var result = new byte[width * height];
        for (int i = 0; i < dist.Length; i++) result[i] = (byte)Math.Min(255, dist[i]);
        return result;
    }
}
