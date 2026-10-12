namespace KhaozEngine.WorldMap.Raster;

/// <summary>Defines the map-space margins needed by raster sampling and cache invalidation.</summary>
internal static class WorldMapRasterReach
{
    /// <summary>Covers building shadows, widened walls and one raster pixel beyond an authored footprint.</summary>
    internal static float FootprintMetres(WorldMapStyle style) =>
        style.BuildingShadowOffsetMetres.Length() + style.WallWidthMetres / 2f + 1f / style.EffectivePixelsPerMetre;

    /// <summary>Covers surface interpolation, relief sampling and shoreline neighbours.</summary>
    internal static float SurfaceMetres(WorldMapStyle style, float cellSize) =>
        2f * cellSize + style.Water.ShorelinePixels / style.EffectivePixelsPerMetre;
}
