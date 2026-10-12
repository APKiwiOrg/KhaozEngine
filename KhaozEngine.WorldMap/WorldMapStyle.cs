using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap;

/// <summary>The terrain relief lighting and steep-slope shading settings.</summary>
/// <param name="AzimuthDegrees">Light direction in degrees clockwise from map north.</param>
/// <param name="AltitudeDegrees">Light altitude above the map plane, in degrees.</param>
/// <param name="Strength">The relief shading strength.</param>
/// <param name="SteepSlope">The slope threshold for additional darkening.</param>
/// <param name="SteepDarken">The colour multiplier for steep slopes.</param>
public sealed record WorldMapRelief(
    float AzimuthDegrees = 315f, float AltitudeDegrees = 45f, float Strength = 0.5f, float SteepSlope = 1f, float SteepDarken = 0.75f);

/// <summary>The depth palette and shoreline appearance of surface water.</summary>
/// <param name="Shallow">The colour at zero water depth.</param>
/// <param name="Deep">The colour at and beyond the fully deep depth.</param>
/// <param name="Shoreline">The shoreline colour.</param>
/// <param name="DeepDepthMetres">The depth in metres at which water reaches the deep colour.</param>
/// <param name="ShorelinePixels">The shoreline width in raster pixels.</param>
public sealed record WorldMapWater(Color Shallow, Color Deep, Color Shoreline, float DeepDepthMetres = 4f, int ShorelinePixels = 1);

/// <summary>A game's immutable world-map style, with neutral engine defaults.</summary>
public sealed record WorldMapStyle
{
    /// <summary>The raster resolution in pixels per map metre. Defaults to four.</summary>
    public float PixelsPerMetre { get; init; } = 4f;

    /// <summary>The square chunk edge length in map metres. Defaults to 64.</summary>
    public float ChunkMetres { get; init; } = 64f;

    /// <summary>The rounded chunk edge length in raster pixels.</summary>
    public int ChunkPixels => (int)MathF.Round(ChunkMetres * PixelsPerMetre);

    /// <summary>The colour outside the authored surface.</summary>
    public Color VoidColor { get; init; } = Color.FromHex("#d9cba6");

    /// <summary>The terrain relief settings.</summary>
    public WorldMapRelief Relief { get; init; } = new();

    /// <summary>The surface-water palette and shoreline settings.</summary>
    public WorldMapWater Water { get; init; } = new(
        Color.FromHex("#6f9cc0"), Color.FromHex("#2a5a9a"), Color.FromHex("#1f3f66"));

    /// <summary>Ordered footprint symbol rules. The first match wins. Defaults to an empty array.</summary>
    public IReadOnlyList<WorldMapSymbolRule> SymbolRules { get; init; } = Array.Empty<WorldMapSymbolRule>();

    /// <summary>The symbol for an unmatched blocking footprint. Unmatched non-blocking footprints are hidden.</summary>
    public WorldMapSymbol FallbackBlocking { get; init; } = new(
        WorldMapSymbolKind.Obstacle, Color.FromHex("#6f6a60"), Color.FromHex("#3d3a34"));

    /// <summary>The shadow colour for building symbols.</summary>
    public Color BuildingShadow { get; init; } = new(0f, 0f, 0f, 0.35f);

    /// <summary>The building shadow offset in map metres, east and south by default.</summary>
    public Vector2 BuildingShadowOffsetMetres { get; init; } = new(0.6f, -0.6f);

    /// <summary>The wall symbol width in map metres.</summary>
    public float WallWidthMetres { get; init; } = 0.4f;

    /// <summary>The symbol outline width in raster pixels.</summary>
    public float OutlinePixels { get; init; } = 1f;

    /// <summary>The footprint and marker icon rules. Defaults to an empty array.</summary>
    public IReadOnlyList<WorldMapIconRule> IconRules { get; init; } = Array.Empty<WorldMapIconRule>();

    /// <summary>The marker label rules, or null when labels are disabled.</summary>
    public WorldMapLabelRules? Labels { get; init; }
}
