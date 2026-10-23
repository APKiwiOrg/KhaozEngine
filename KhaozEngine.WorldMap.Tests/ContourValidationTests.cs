using System;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Invalid contour options fail before sampling at both raster pipeline boundaries.</summary>
public sealed class ContourValidationTests
{
    /// <summary>Intervals and widths must stay finite and strictly positive.</summary>
    [Theory]
    [InlineData(true, 0f)]
    [InlineData(true, -1f)]
    [InlineData(true, float.NaN)]
    [InlineData(true, float.PositiveInfinity)]
    [InlineData(true, float.NegativeInfinity)]
    [InlineData(false, 0f)]
    [InlineData(false, -1f)]
    [InlineData(false, float.NaN)]
    [InlineData(false, float.PositiveInfinity)]
    [InlineData(false, float.NegativeInfinity)]
    public void Contours_InvalidNumbers_AreRejected(bool interval, float value)
    {
        var source = new TestWorldMapSource(new(0f, 0f, 1f, 1f));
        WorldMapStyle style = interval ? ContourTests.Style(value) : ContourTests.Style(width: value);
        string property = interval ? "Contours.IntervalMetres" : "Contours.WidthPixels";
        ArgumentException raster = Assert.Throws<ArgumentException>(() => WorldMapRasterizer.Rasterize(source, style, new(0, 0)));
        Assert.Equal(property, raster.ParamName);
        Assert.Equal(0, source.SampleCalls);
        ArgumentException cache = Assert.Throws<ArgumentException>(() =>
        {
            using var map = new WorldMapChunkCache(source, style);
        });
        Assert.Equal(property, cache.ParamName);
    }
}
