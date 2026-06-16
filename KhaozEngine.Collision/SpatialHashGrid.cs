using System;
using System.Collections.Generic;
using System.Numerics;

namespace KhaozEngine.Collision;

/// <summary>
/// Uniform spatial hash grid for broadphase queries. Stores caller-supplied integer indices in a per-cell
/// singly-linked free-list (head insertion, so chains are LIFO) and answers radius queries by walking the
/// covered cells. Iteration order and float math are deterministic and must stay bit-identical for lockstep
/// sims: cell coordinate = <c>(int)MathF.Floor(world / cellSize)</c>, queries walk Y outer / X inner.
/// </summary>
/// <remarks>
/// Rebuild each tick with <see cref="BeginRebuild"/> then one <see cref="Add"/> per live item, in the order
/// the indices should chain. The grid is generic over the item collection: the index you pass to
/// <see cref="Add"/> is exactly what <see cref="GetQueryIndex"/> hands back, so it can index whatever rows or
/// array the caller owns. Pass a <paramref name="capacity"/> to <see cref="BeginRebuild"/> that exceeds the
/// largest index you will add.
/// </remarks>
public sealed class SpatialHashGrid
{
    private readonly float cellSize;
    private readonly Dictionary<long, int> cellHeads = new();
    private int[] next = Array.Empty<int>();
    private int[] queryIndices = Array.Empty<int>();
    private float maxItemRadius;

    /// <summary>Creates a grid with the given cell size (clamped to a minimum of 1).</summary>
    public SpatialHashGrid(float cellSize)
    {
        this.cellSize = MathF.Max(1f, cellSize);
    }

    /// <summary>Empties the grid without changing its backing capacity.</summary>
    public void Clear()
    {
        cellHeads.Clear();
        maxItemRadius = 0f;
    }

    /// <summary>
    /// Begins a rebuild for up to <paramref name="capacity"/> items: ensures backing capacity, clears the
    /// cells, resets the max-radius accumulator, and resets the link chains. Follow with one <see cref="Add"/>
    /// call per item. <paramref name="capacity"/> must exceed the largest index passed to <see cref="Add"/>.
    /// </summary>
    public void BeginRebuild(int capacity)
    {
        EnsureCapacity(capacity);
        cellHeads.Clear();
        maxItemRadius = 0f;

        for (int i = 0; i < capacity; i++)
        {
            next[i] = -1;
        }
    }

    /// <summary>
    /// Inserts an item at the given <paramref name="index"/> into its cell. Head insertion makes each cell's
    /// chain LIFO. Call once per item between <see cref="BeginRebuild"/> and the first query.
    /// </summary>
    public void Add(int index, Vector2 position, float radius)
    {
        if (radius > maxItemRadius)
        {
            maxItemRadius = radius;
        }

        int cellX = GetCellCoordinate(position.X);
        int cellY = GetCellCoordinate(position.Y);
        long cellKey = PackCellKey(cellX, cellY);

        if (cellHeads.TryGetValue(cellKey, out int headIndex))
        {
            next[index] = headIndex;
        }

        cellHeads[cellKey] = index;
    }

    /// <summary>
    /// Collects the indices of all items whose cells fall within <paramref name="queryRadius"/> (expanded by
    /// the largest item radius seen this rebuild) around <paramref name="center"/>. Returns the candidate
    /// count; read each via <see cref="GetQueryIndex"/>. Candidates are a superset (cell granularity), so the
    /// caller still does the precise per-pair test.
    /// </summary>
    public int QueryCandidates(Vector2 center, float queryRadius)
    {
        if (cellHeads.Count == 0)
        {
            return 0;
        }

        float searchRadius = MathF.Max(0f, queryRadius) + maxItemRadius;
        int minCellX = GetCellCoordinate(center.X - searchRadius);
        int maxCellX = GetCellCoordinate(center.X + searchRadius);
        int minCellY = GetCellCoordinate(center.Y - searchRadius);
        int maxCellY = GetCellCoordinate(center.Y + searchRadius);

        int queryCount = 0;
        for (int y = minCellY; y <= maxCellY; y++)
        {
            for (int x = minCellX; x <= maxCellX; x++)
            {
                long cellKey = PackCellKey(x, y);
                if (!cellHeads.TryGetValue(cellKey, out int itemIndex))
                {
                    continue;
                }

                while (itemIndex >= 0)
                {
                    if (queryCount >= queryIndices.Length)
                    {
                        GrowQueryArray(queryCount + 1);
                    }

                    queryIndices[queryCount++] = itemIndex;
                    itemIndex = next[itemIndex];
                }
            }
        }

        return queryCount;
    }

    /// <summary>Sorts the first <paramref name="count"/> query results ascending, in place.</summary>
    public void SortQueryIndicesAscending(int count)
    {
        if (count > 1)
        {
            Array.Sort(queryIndices, 0, count);
        }
    }

    /// <summary>Returns the item index at the given position in the last query's result buffer.</summary>
    public int GetQueryIndex(int queryPosition)
    {
        return queryIndices[queryPosition];
    }

    private void EnsureCapacity(int itemCount)
    {
        if (next.Length < itemCount)
        {
            int newLength = Math.Max(itemCount, Math.Max(64, next.Length * 2));
            next = new int[newLength];
        }

        if (queryIndices.Length < itemCount)
        {
            int newLength = Math.Max(itemCount, Math.Max(64, queryIndices.Length * 2));
            queryIndices = new int[newLength];
        }
    }

    private void GrowQueryArray(int requiredLength)
    {
        int newLength = Math.Max(requiredLength, Math.Max(64, queryIndices.Length * 2));
        Array.Resize(ref queryIndices, newLength);
    }

    private int GetCellCoordinate(float worldPosition)
    {
        return (int)MathF.Floor(worldPosition / cellSize);
    }

    private static long PackCellKey(int cellX, int cellY)
    {
        return ((long)cellX << 32) | (uint)cellY;
    }
}
