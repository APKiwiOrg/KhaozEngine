using System;
using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// Exact 2D grid segment traversal for line-of-sight / segment-raycast queries. Walks every cell the segment
/// from -&gt; to passes through (an Amanatides&amp;Woo digital differential analyser, i.e. a 4-connected supercover)
/// rather than sampling at fixed intervals, so a thin diagonal wall is never stepped over.
/// </summary>
/// <remarks>
/// Fully decoupled from any game type: the caller supplies a <c>blocks(x, y)</c> / <c>visit(x, y)</c> predicate
/// so it decides what a cell means. Coordinates are world units; cell <c>(x, y)</c> spans
/// <c>[x*cellSize, (x+1)*cellSize)</c> on each axis with the grid origin at <c>(0, 0)</c>. Cell coordinate =
/// <c>(int)MathF.Floor(world / cellSize)</c>, matching <see cref="SpatialHashGrid"/>; negative coordinates floor
/// toward -infinity as expected. The math is deterministic and the hot path allocates nothing (no
/// <c>IEnumerable</c>/yield) - the only delegate is the caller's predicate.
/// <para>
/// On an exact corner crossing (the segment passing through a shared grid corner) the traversal steps the X axis
/// first, so it threads through the edge-adjacent cell rather than jumping diagonally; the visited path is always
/// 4-connected (each step changes one axis by one).
/// </para>
/// </remarks>
public static class GridRay
{
    /// <summary>
    /// Returns true if the segment from <paramref name="from"/> to <paramref name="to"/> crosses no cell for which
    /// <paramref name="blocks"/> is true. By default the two endpoint cells (the cells containing
    /// <paramref name="from"/> and <paramref name="to"/>) are NOT tested - a shooter standing in a wall cell, or a
    /// target inside one, does not block its own line. Set <paramref name="includeEndpointCells"/> to also test
    /// those two cells. A zero-length segment is always clear (its single cell is an endpoint).
    /// </summary>
    /// <param name="from">Segment start, in world units.</param>
    /// <param name="to">Segment end, in world units.</param>
    /// <param name="cellSize">Width/height of a grid cell in world units; must be positive.</param>
    /// <param name="blocks">Predicate returning true for a cell that blocks the line.</param>
    /// <param name="includeEndpointCells">When true, the start and end cells are tested too (default false).</param>
    public static bool IsClear(
        Vector2 from, Vector2 to, float cellSize, Func<int, int, bool> blocks, bool includeEndpointCells = false)
    {
        if (blocks is null)
        {
            throw new ArgumentNullException(nameof(blocks));
        }

        if (cellSize <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
        }

        var walker = new Walker(from, to, cellSize);
        int lastIndex = walker.TotalSteps;
        int index = 0;

        while (true)
        {
            bool isEndpoint = index == 0 || index == lastIndex;
            if ((includeEndpointCells || !isEndpoint) && blocks(walker.X, walker.Y))
            {
                return false;
            }

            if (!walker.MoveNext())
            {
                break;
            }

            index++;
        }

        return true;
    }

    /// <summary>
    /// Enumerates, in order, every cell the segment from <paramref name="from"/> to <paramref name="to"/> touches -
    /// including both endpoint cells - calling <paramref name="visit"/> for each. Stop early by returning false from
    /// <paramref name="visit"/>. Returns true if the whole segment was traversed, false if a visit stopped it early.
    /// Allocation-free apart from the caller's delegate.
    /// </summary>
    /// <param name="from">Segment start, in world units.</param>
    /// <param name="to">Segment end, in world units.</param>
    /// <param name="cellSize">Width/height of a grid cell in world units; must be positive.</param>
    /// <param name="visit">Per-cell callback; return false to stop the traversal.</param>
    public static bool Trace(Vector2 from, Vector2 to, float cellSize, Func<int, int, bool> visit)
    {
        if (visit is null)
        {
            throw new ArgumentNullException(nameof(visit));
        }

        if (cellSize <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize, "Cell size must be positive.");
        }

        var walker = new Walker(from, to, cellSize);

        while (true)
        {
            if (!visit(walker.X, walker.Y))
            {
                return false;
            }

            if (!walker.MoveNext())
            {
                break;
            }
        }

        return true;
    }

    /// <summary>
    /// Stack-only Amanatides&amp;Woo DDA cursor. Starts on the cell containing <c>from</c>; <see cref="MoveNext"/>
    /// advances one cell along the segment and returns false once the cell containing <c>to</c> has been reached.
    /// Total advances equal the Manhattan distance between the start and end cells, so termination is exact and not
    /// subject to float drift on the final boundary.
    /// </summary>
    private struct Walker
    {
        private int x;
        private int y;
        private readonly int stepX;
        private readonly int stepY;
        private float tMaxX;
        private float tMaxY;
        private readonly float tDeltaX;
        private readonly float tDeltaY;
        private int remaining;

        public readonly int X => x;
        public readonly int Y => y;
        public readonly int TotalSteps => totalSteps;

        private readonly int totalSteps;

        public Walker(Vector2 from, Vector2 to, float cellSize)
        {
            x = (int)MathF.Floor(from.X / cellSize);
            y = (int)MathF.Floor(from.Y / cellSize);
            int endX = (int)MathF.Floor(to.X / cellSize);
            int endY = (int)MathF.Floor(to.Y / cellSize);

            float dx = to.X - from.X;
            float dy = to.Y - from.Y;

            if (dx > 0f)
            {
                stepX = 1;
                tDeltaX = cellSize / dx;
                tMaxX = ((x + 1) * cellSize - from.X) / dx;
            }
            else if (dx < 0f)
            {
                stepX = -1;
                tDeltaX = cellSize / -dx;
                tMaxX = (x * cellSize - from.X) / dx;
            }
            else
            {
                stepX = 0;
                tDeltaX = float.PositiveInfinity;
                tMaxX = float.PositiveInfinity;
            }

            if (dy > 0f)
            {
                stepY = 1;
                tDeltaY = cellSize / dy;
                tMaxY = ((y + 1) * cellSize - from.Y) / dy;
            }
            else if (dy < 0f)
            {
                stepY = -1;
                tDeltaY = cellSize / -dy;
                tMaxY = (y * cellSize - from.Y) / dy;
            }
            else
            {
                stepY = 0;
                tDeltaY = float.PositiveInfinity;
                tMaxY = float.PositiveInfinity;
            }

            totalSteps = Math.Abs(endX - x) + Math.Abs(endY - y);
            remaining = totalSteps;
        }

        public bool MoveNext()
        {
            if (remaining <= 0)
            {
                return false;
            }

            // Tie (exact corner) prefers the X step, keeping the path 4-connected and deterministic.
            if (tMaxX <= tMaxY)
            {
                tMaxX += tDeltaX;
                x += stepX;
            }
            else
            {
                tMaxY += tDeltaY;
                y += stepY;
            }

            remaining--;
            return true;
        }
    }
}
