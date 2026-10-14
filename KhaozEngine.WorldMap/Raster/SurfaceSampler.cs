using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap.Raster;

internal readonly record struct SurfaceSample(bool HasGround, Color Ground, float Height, float Wetness, float WaterSurface)
{
    internal bool IsWet => HasGround && Wetness >= 0.5f;
}

/// <summary>Resamples source cell centres, omitting void contributions and preserving nearest-cell void ownership.</summary>
internal sealed class SurfaceSampler(WorldMapSurfaceGrid grid)
{
    internal float CellSize => grid.CellSize;

    internal SurfaceSample Sample(Vector2 point)
    {
        float column = (point.X - grid.Origin.X) / grid.CellSize;
        float row = (point.Y - grid.Origin.Y) / grid.CellSize;
        if (!HasGround((int)MathF.Floor(column), (int)MathF.Floor(row))) return default;

        float centreColumn = column - 0.5f;
        float centreRow = row - 0.5f;
        int left = (int)MathF.Floor(centreColumn);
        int bottom = (int)MathF.Floor(centreRow);
        float fx = centreColumn - left;
        float fy = centreRow - bottom;
        Vector4 ground = Vector4.Zero;
        float height = 0f, total = 0f, wetWeight = 0f, water = 0f;
        for (int dy = 0; dy < 2; dy++)
            for (int dx = 0; dx < 2; dx++)
            {
                int x = left + dx, y = bottom + dy;
                if (!HasGround(x, y)) continue;
                float weight = (dx == 0 ? 1f - fx : fx) * (dy == 0 ? 1f - fy : fy);
                if (weight <= 0f) continue;
                int index = grid.Index(x, y);
                ground += grid.Ground[index].ToVector4() * weight;
                height += grid.SurfaceHeight[index] * weight;
                total += weight;
                if (float.IsFinite(grid.WaterSurface[index]))
                {
                    wetWeight += weight;
                    water += grid.WaterSurface[index] * weight;
                }
            }

        return new SurfaceSample(true, Color.FromVector4(ground / total), height / total,
            wetWeight / total, wetWeight > 0f ? water / wetWeight : float.NaN);
    }

    bool HasGround(int column, int row)
    {
        if (column < 0 || column >= grid.Width || row < 0 || row >= grid.Height) return false;
        int index = grid.Index(column, row);
        return grid.Ground[index].A > 0f && float.IsFinite(grid.SurfaceHeight[index]);
    }
}
