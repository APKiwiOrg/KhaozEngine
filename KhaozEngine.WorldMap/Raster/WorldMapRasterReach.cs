using System;

namespace KhaozEngine.WorldMap.Raster;

/// <summary>Defines the map-space margins needed by raster sampling and cache invalidation.</summary>
internal static class WorldMapRasterReach
{
    /// <summary>Covers building shadows, widened walls and one raster pixel beyond an authored footprint.</summary>
    internal static float FootprintMetres(WorldMapStyle style) => FootprintMetres(style, 0);

    /// <summary>Covers footprints at the selected level's raster scale.</summary>
    internal static float FootprintMetres(WorldMapStyle style, int level) =>
        style.BuildingShadowOffsetMetres.Length() + style.WallWidthMetres / 2f + 1f / style.LevelPixelsPerMetre(level);

    /// <summary>Covers surface interpolation, relief sampling and shoreline neighbours.</summary>
    internal static float SurfaceMetres(WorldMapStyle style, float cellSize) => SurfaceMetres(style, cellSize, 0);

    /// <summary>Covers the returned sampling cells and shoreline neighbours at the selected raster level.</summary>
    internal static float SurfaceMetres(WorldMapStyle style, float cellSize, int level) =>
        2f * cellSize + style.Water.ShorelinePixels / style.LevelPixelsPerMetre(level);

    /// <summary>Resolves the source contract's coarsest native multiple within a resolution hint.</summary>
    internal static float SampleCellSize(float nativeCellSize, float maxCellSize) =>
        MathF.Max(1f, MathF.Floor(maxCellSize / nativeCellSize)) * nativeCellSize;
}
