using System;
using KhaozEngine.WorldMap.Raster;

namespace KhaozEngine.WorldMap;

/// <summary>Rejects unusable raster geometry and shading settings at public pipeline boundaries.</summary>
internal static class WorldMapStyleValidation
{
    internal static void Validate(WorldMapStyle style)
    {
        Require(float.IsFinite(style.PixelsPerMetre) && style.PixelsPerMetre > 0f,
            nameof(style.PixelsPerMetre), "must be finite and positive");
        Require(float.IsFinite(style.ChunkMetres) && style.ChunkMetres > 0f,
            nameof(style.ChunkMetres), "must be finite and positive");
        Require(style.ChunkPixels >= 1, nameof(style.ChunkPixels), "must round to at least one pixel");
        Require(float.IsFinite(style.Relief.AltitudeDegrees) && style.Relief.AltitudeDegrees > 0f && style.Relief.AltitudeDegrees <= 90f,
            "Relief.AltitudeDegrees", "must be finite and in (0, 90]");
        Require(UnitInterval(style.Relief.Strength), "Relief.Strength", "must be finite and in [0, 1]");
        Require(NonNegative(style.Relief.SteepSlope), "Relief.SteepSlope", "must be finite and non-negative");
        Require(UnitInterval(style.Relief.SteepDarken), "Relief.SteepDarken", "must be finite and in [0, 1]");
        Require(float.IsFinite(style.Water.DeepDepthMetres) && style.Water.DeepDepthMetres > 0f,
            "Water.DeepDepthMetres", "must be finite and positive");
        Require(style.Water.ShorelinePixels >= 0, "Water.ShorelinePixels", "must be non-negative");
        Require(NonNegative(style.WallWidthMetres), nameof(style.WallWidthMetres), "must be finite and non-negative");
        Require(NonNegative(style.OutlinePixels), nameof(style.OutlinePixels), "must be finite and non-negative");
        Require(float.IsFinite(style.BuildingShadowOffsetMetres.X) && float.IsFinite(style.BuildingShadowOffsetMetres.Y),
            nameof(style.BuildingShadowOffsetMetres), "must have finite components");
        WorldMapSymbolSizing.Validate(style.FallbackBlocking);
        foreach (WorldMapSymbolRule rule in style.SymbolRules)
            WorldMapSymbolSizing.Validate(rule.Symbol);
    }

    static bool UnitInterval(float value) => float.IsFinite(value) && value >= 0f && value <= 1f;

    static bool NonNegative(float value) => float.IsFinite(value) && value >= 0f;

    static void Require(bool valid, string property, string requirement)
    {
        if (!valid) throw new ArgumentException($"{property} {requirement}.", property);
    }
}
