using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Invalid style settings fail at both public pipeline boundaries before any surface read.</summary>
public sealed class StyleValidationTests
{
    /// <summary>Resolution, chunk size and the rounded raster dimension must be usable.</summary>
    [Theory]
    [InlineData("PixelsPerMetre", 0f)]
    [InlineData("PixelsPerMetre", -1f)]
    [InlineData("PixelsPerMetre", float.NaN)]
    [InlineData("PixelsPerMetre", float.PositiveInfinity)]
    [InlineData("PixelsPerMetre", float.NegativeInfinity)]
    [InlineData("ChunkMetres", 0f)]
    [InlineData("ChunkMetres", -1f)]
    [InlineData("ChunkMetres", float.NaN)]
    [InlineData("ChunkMetres", float.PositiveInfinity)]
    [InlineData("ChunkMetres", float.NegativeInfinity)]
    [InlineData("ChunkPixels", 0.1f)]
    public void RasterDimensions_InvalidValues_AreRejected(string property, float value)
    {
        var style = new WorldMapStyle { ChunkMetres = 3f };
        style = property == "ChunkMetres" ? style with { ChunkMetres = value } : style with { PixelsPerMetre = value };
        Reject(style, property);
    }

    /// <summary>Relief altitude cannot produce a zero or non-finite lighting divisor.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(90.1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ReliefAltitude_InvalidValues_AreRejected(float value) =>
        Reject(new() { Relief = new(AltitudeDegrees: value) }, "Relief.AltitudeDegrees");

    /// <summary>Relief strength and steep darkening remain finite within the unit interval.</summary>
    [Theory]
    [InlineData("Strength", -0.1f)]
    [InlineData("Strength", 1.1f)]
    [InlineData("Strength", float.NaN)]
    [InlineData("Strength", float.PositiveInfinity)]
    [InlineData("Strength", float.NegativeInfinity)]
    [InlineData("SteepDarken", -0.1f)]
    [InlineData("SteepDarken", 1.1f)]
    [InlineData("SteepDarken", float.NaN)]
    [InlineData("SteepDarken", float.PositiveInfinity)]
    [InlineData("SteepDarken", float.NegativeInfinity)]
    public void ReliefMultipliers_InvalidValues_AreRejected(string property, float value)
    {
        WorldMapRelief relief = property == "Strength" ? new(Strength: value) : new(SteepDarken: value);
        Reject(new() { Relief = relief }, "Relief." + property);
    }

    /// <summary>The steepness threshold is finite and non-negative.</summary>
    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ReliefSlope_InvalidValues_AreRejected(float value) =>
        Reject(new() { Relief = new(SteepSlope: value) }, "Relief.SteepSlope");

    /// <summary>Water depth normalization is finite and strictly positive.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void WaterDepth_InvalidValues_AreRejected(float value)
    {
        var style = new WorldMapStyle();
        Reject(style with { Water = style.Water with { DeepDepthMetres = value } }, "Water.DeepDepthMetres");
    }

    /// <summary>Shoreline sampling cannot have a negative pixel radius.</summary>
    [Fact]
    public void WaterShoreline_NegativeRadius_IsRejected()
    {
        var style = new WorldMapStyle();
        Reject(style with { Water = style.Water with { ShorelinePixels = -1 } }, "Water.ShorelinePixels");
    }

    /// <summary>Symbol widths cannot turn geometry or raster reach negative or non-finite.</summary>
    [Theory]
    [InlineData("WallWidthMetres", -1f)]
    [InlineData("WallWidthMetres", float.NaN)]
    [InlineData("WallWidthMetres", float.PositiveInfinity)]
    [InlineData("WallWidthMetres", float.NegativeInfinity)]
    [InlineData("OutlinePixels", -1f)]
    [InlineData("OutlinePixels", float.NaN)]
    [InlineData("OutlinePixels", float.PositiveInfinity)]
    [InlineData("OutlinePixels", float.NegativeInfinity)]
    public void SymbolWidths_InvalidValues_AreRejected(string property, float value)
    {
        var style = new WorldMapStyle();
        Reject(property == "WallWidthMetres" ? style with { WallWidthMetres = value } : style with { OutlinePixels = value }, property);
    }

    /// <summary>Both components of the shadow offset must be finite.</summary>
    [Theory]
    [InlineData(float.NaN, 0f)]
    [InlineData(float.PositiveInfinity, 0f)]
    [InlineData(float.NegativeInfinity, 0f)]
    [InlineData(0f, float.NaN)]
    [InlineData(0f, float.PositiveInfinity)]
    [InlineData(0f, float.NegativeInfinity)]
    public void ShadowOffset_NonFiniteComponents_AreRejected(float x, float y) =>
        Reject(new() { BuildingShadowOffsetMetres = new(x, y) }, "BuildingShadowOffsetMetres");

    /// <summary>Inclusive valid limits keep flat ground finite and allow cache construction.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    public void ValidRangeLimits_AreAccepted(float multiplier)
    {
        var style = new WorldMapStyle
        {
            ChunkMetres = 1f,
            PixelsPerMetre = 1f,
            Relief = new(AltitudeDegrees: 90f, Strength: multiplier, SteepSlope: 0f, SteepDarken: multiplier),
            WallWidthMetres = 0f,
            OutlinePixels = 0f,
            BuildingShadowOffsetMetres = Vector2.Zero,
            Water = new WorldMapStyle().Water with { ShorelinePixels = 0 }
        };
        var source = new TestWorldMapSource(new(0f, 0f, 1f, 1f));
        source.SetCell(0, 0, Color.White, 0f);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, WorldMapRasterizer.Rasterize(source, style, new(0, 0)).Rgba);
        using var cache = new WorldMapChunkCache(source, style);
        Assert.Same(style, cache.Style);
    }

    static void Reject(WorldMapStyle style, string property)
    {
        var source = new TestWorldMapSource(new(0f, 0f, 1f, 1f));
        ArgumentException rasterError = Assert.Throws<ArgumentException>(() => WorldMapRasterizer.Rasterize(source, style, new(0, 0)));
        Assert.Equal(property, rasterError.ParamName);
        Assert.Equal(0, source.SampleCalls);
        ArgumentException cacheError = Assert.Throws<ArgumentException>(() =>
        {
            using var cache = new WorldMapChunkCache(source, style);
        });
        Assert.Equal(property, cacheError.ParamName);
    }
}
