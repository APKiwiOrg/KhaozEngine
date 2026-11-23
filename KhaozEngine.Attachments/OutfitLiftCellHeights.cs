using System;
using System.Buffers;

namespace KhaozEngine.Attachments;

/// <summary>Temporary cell heights for the current outfit, with a read-only view of strictly lower layers.</summary>
internal sealed class OutfitLiftCellHeights : IDisposable
{
    const float NoHeight = float.NegativeInfinity;

    readonly byte[] triangleRegions;
    readonly float[] highest;
    readonly float[] below;
    readonly int cellCount;
    readonly RegionSet occupiedRegions;

    internal OutfitLiftCellHeights(BodyRegionMap body, int triangleCount)
    {
        triangleRegions = new byte[triangleCount];
        int offset = 0;
        RegionSet occupied = RegionSet.Empty;
        for (int part = 0; part < body.Parts.Count; part++)
        {
            int count = body.TriangleCount(part);
            body.WriteTriangleRegions(part, triangleRegions.AsSpan(offset, count));
            offset += count;
            foreach (RegionRun run in body.Parts[part])
                occupied = occupied.With(run.Region);
        }
        occupiedRegions = occupied;

        cellCount = checked(triangleCount * BodyCell.CellsPerTriangle);
        highest = ArrayPool<float>.Shared.Rent(cellCount);
        try
        {
            below = ArrayPool<float>.Shared.Rent(cellCount);
        }
        catch
        {
            ArrayPool<float>.Shared.Return(highest);
            throw;
        }
        highest.AsSpan(0, cellCount).Fill(NoHeight);
    }

    internal void Freeze() => highest.AsSpan(0, cellCount).CopyTo(below);

    internal bool HasCells(int region) => occupiedRegions.Contains(region);

    internal float? HeightBelow(LiftFootprintTriangle triangle)
    {
        float height = NoHeight;
        foreach (int key in triangle.CellKeys)
            height = MathF.Max(height, below[key]);
        return height == NoHeight ? null : height;
    }

    internal void AddTriangle(LiftFootprintTriangle triangle, float lift, Span<float?> effective)
    {
        ReadOnlySpan<int> keys = triangle.CellKeys;
        ReadOnlySpan<ushort> heights = triangle.HeightUnits;
        for (int entry = 0; entry < heights.Length; entry++)
        {
            int key = keys[entry];
            float height = (float)LiftFootprintTriangle.ToMetres(heights[entry]) + lift;
            highest[key] = MathF.Max(highest[key], height);
            int region = triangleRegions[key / BodyCell.CellsPerTriangle];
            effective[region] = MathF.Max(effective[region] ?? NoHeight, height);
        }
    }

    internal void AddRegional(ReadOnlySpan<float?> effective)
    {
        for (int triangle = 0; triangle < triangleRegions.Length; triangle++)
        {
            if (effective[triangleRegions[triangle]] is not { } height)
                continue;
            Span<float> cells = highest.AsSpan(triangle * BodyCell.CellsPerTriangle, BodyCell.CellsPerTriangle);
            for (int cell = 0; cell < cells.Length; cell++)
                cells[cell] = MathF.Max(cells[cell], height);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        ArrayPool<float>.Shared.Return(below);
        ArrayPool<float>.Shared.Return(highest);
    }
}
