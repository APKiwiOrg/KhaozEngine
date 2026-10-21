using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Contours follow metre heights and keep their width in raster pixels.</summary>
public sealed class ContourTests
{
    /// <summary>A two-metres-per-metre ramp crosses a four-metre interval every two map metres.</summary>
    [Fact]
    public void Contours_Ramp_UsesActualHeightIntervalsAndAntialiasing()
    {
        TestWorldMapSource source = Surface((x, _) => 2f * x);
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style(4f, 0.75f), new(0, 0));

        Assert.InRange(Red(image, 7, 16), (byte)155, (byte)165);
        Assert.InRange(Red(image, 8, 16), (byte)155, (byte)165);
        Assert.Equal(255, Red(image, 3, 16));
        Assert.Equal(Red(image, 7, 16), Red(image, 15, 16));
        Assert.Equal(Red(image, 8, 16), Red(image, 16, 16));
    }

    /// <summary>Diagonal crossings retain their antialias fringe at the configured perpendicular width.</summary>
    [Fact]
    public void Contours_DiagonalRamp_PreservesStrokeFringe()
    {
        TestWorldMapSource source = Surface((x, y) => x + y + 8f);
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style(100f, 0.75f), new(-1, -1));

        Assert.InRange(Red(image, 17, 16), (byte)210, (byte)214);
        Assert.Equal(Red(image, 17, 16), Red(image, 16, 17));
        Assert.Equal(255, Red(image, 18, 16));
    }

    /// <summary>Opposing diagonal slopes keep a contour through their stationary saddle intersection.</summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(-1f)]
    public void Contours_StationarySaddle_PaintsTheCrossing(float direction)
    {
        TestWorldMapSource source = Surface((x, y) => direction * (x - 4.125f) * (y - 4.125f));
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style(1f, 0.75f), new(0, 0));

        Assert.InRange(Red(image, 16, 15), (byte)0, (byte)64);
        Assert.InRange(Red(image, 16, 14), (byte)0, (byte)64);
        Assert.InRange(Red(image, 17, 15), (byte)0, (byte)64);
    }

    /// <summary>Distant opposing slopes cannot ink a locally flat interval plateau.</summary>
    [Fact]
    public void Contours_StationaryPlateauBetweenOpposingSlopes_DoesNotPaint()
    {
        TestWorldMapSource source = Surface((x, y) =>
            MathF.Sign(x - 4.125f) * MathF.Max(0f, MathF.Abs(x - 4.125f) - 0.75f) *
            MathF.Sign(y - 4.125f) * MathF.Max(0f, MathF.Abs(y - 4.125f) - 0.75f));
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style(1f, 16f), new(0, 0));

        Assert.Equal(255, Red(image, 16, 15));
    }

    /// <summary>The width stays two image pixels as the pyramid changes its metre scale.</summary>
    [Theory]
    [InlineData(0, 15, 16)]
    [InlineData(1, 7, 8)]
    [InlineData(2, 3, 4)]
    public void Contours_WidthPixels_IsIndependentOfRasterLevel(int level, int left, int right)
    {
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(Surface((x, _) => x), Style(4f, 2f), new(0, 0, level));

        Assert.Equal(0, Red(image, left, 16));
        Assert.Equal(0, Red(image, right, 16));
        Assert.Equal(255, Red(image, left - 1, 16));
        Assert.Equal(255, Red(image, right + 1, 16));
    }

    /// <summary>Contour alpha composites over the shaded ground before footprint symbols.</summary>
    [Fact]
    public void Contours_AlphaAndFootprints_UseExistingCompositionOrder()
    {
        TestWorldMapSource source = Surface((x, _) => x);
        WorldMapStyle style = Style(4f, 2f, new(0f, 0f, 0f, 0.5f));
        WorldMapChunkImage bare = WorldMapRasterizer.Rasterize(source, style, new(0, 0));
        Assert.Equal(128, Red(bare, 15, 16));
        source.Footprints.Add(new(1, "roof", new(4f, 4f), new(1f), 0f, [], true, WorldMapCollision.Solid));
        style = style with
        {
            BuildingShadow = Color.Transparent,
            OutlinePixels = 0f,
            SymbolRules = [new(new(IsRoof: true), new(WorldMapSymbolKind.Building, Color.White, Color.White))]
        };
        WorldMapChunkImage covered = WorldMapRasterizer.Rasterize(source, style, new(0, 0));
        Assert.Equal(255, Red(covered, 15, 16));
    }

    /// <summary>Water and void receive exactly their existing bytes even on a contour-crossing ramp.</summary>
    [Fact]
    public void Contours_WaterAndVoid_AreUnchanged()
    {
        TestWorldMapSource source = Surface((x, _) => x, (x, _) => x < 4f ? 20f : float.NaN);
        for (int row = 0; row < 192; row++)
            for (int column = 80; column < 192; column++)
                source.SetCell(column, row, Color.Transparent, float.NaN);
        WorldMapStyle style = Style(2f, 3f);
        WorldMapChunkImage contours = WorldMapRasterizer.Rasterize(source, style, new(0, 0));
        WorldMapChunkImage baseline = WorldMapRasterizer.Rasterize(source, style with { Contours = null }, new(0, 0));
        for (int row = 0; row < 32; row++)
            for (int column = 0; column < 32; column++)
                if (column < 16)
                    Assert.Equal(Pixel(baseline, column, row), Pixel(contours, column, row));
        WorldMapChunkImage voidContour = WorldMapRasterizer.Rasterize(source, style, new(1, 0));
        WorldMapChunkImage voidBaseline = WorldMapRasterizer.Rasterize(source, style with { Contours = null }, new(1, 0));
        Assert.Equal(voidBaseline.Rgba, voidContour.Rgba);
    }

    /// <summary>A plateau exactly at an interval cannot turn into an inked area or boundary stripe.</summary>
    [Fact]
    public void Contours_FlatIntervalPlateau_DoesNotPaint()
    {
        TestWorldMapSource source = Surface((x, _) => MathF.Min(4f, x));
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style(4f, 2f), new(0, 0));
        for (int column = 13; column < 32; column++)
            Assert.Equal(255, Red(image, column, 16));
        WorldMapChunkImage flat = WorldMapRasterizer.Rasterize(Surface((_, _) => 4f), Style(4f, 2f), new(0, 0));
        Assert.All(flat.Rgba, channel => Assert.Equal(255, channel));
    }

    internal static TestWorldMapSource Surface(Func<float, float, float> height,
        Func<float, float, float>? water = null)
    {
        var source = new TestWorldMapSource(new(-32f, -32f, 96f, 96f), 0.5f);
        for (int row = 0; row < 192; row++)
            for (int column = 0; column < 192; column++)
            {
                float x = -32f + (column + 0.5f) * 0.5f;
                float y = -32f + (row + 0.5f) * 0.5f;
                source.SetCell(column, row, Color.White, height(x, y), water?.Invoke(x, y) ?? float.NaN);
            }
        return source;
    }

    internal static WorldMapStyle Style(float interval = 4f, float width = 0.75f, Color? color = null)
    {
        var style = new WorldMapStyle
        {
            ChunkMetres = 8f,
            PixelsPerMetre = 4f,
            Relief = new(Strength: 0f, SteepDarken: 1f),
            BuildingShadowOffsetMetres = Vector2.Zero,
            WallWidthMetres = 0f,
            Contours = new(interval, color ?? Color.Black, width)
        };
        return style;
    }

    internal static byte Red(WorldMapChunkImage image, int x, int y) => image.Rgba[(y * image.Size + x) * 4];

    internal static byte[] Pixel(WorldMapChunkImage image, int x, int y) =>
        image.Rgba.AsSpan((y * image.Size + x) * 4, 4).ToArray();
}
