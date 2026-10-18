using System;
using System.Numerics;

namespace KhaozEngine.Primitives;

/// <summary>One world-origin lattice for adapter outlines, window edges and roof clipping.</summary>
public static class InteriorLattice
{
    /// <summary>Round an integer lattice index times the cell size exactly once.</summary>
    public static float Edge(double index, float cellSize) => (float)(index * cellSize);

    /// <summary>Reconstruct a window corner on the global lattice, or its explicit off-lattice origin.</summary>
    public static Vector2 Corner(InteriorWindow window, int x, int z) =>
        new(Edge(window.Min.X, window.CellSize, x), Edge(window.Min.Y, window.CellSize, z));

    internal static float Edge(float minimum, float cellSize, int offset)
    {
        double index = Math.Round((double)minimum / cellSize);
        // Explicit off-lattice windows retain their authored minimum. Snapped windows always reconstruct
        // the global index before multiplying, never add a local distance to a rounded world coordinate.
        return Edge(index, cellSize) == minimum
            ? Edge(index + offset, cellSize)
            : (float)(minimum + offset * (double)cellSize);
    }

    internal static int Bound(float coordinate, float minimum, float cellSize, int cells)
    {
        int low = 0, high = cells;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (Edge(minimum, cellSize, middle) < coordinate) low = middle + 1;
            else high = middle;
        }
        return low;
    }
}
