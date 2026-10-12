using System;
using System.Numerics;
using System.Security.Cryptography;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using KhaozEngine.WorldMap.Raster;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Proves terrain sampling, north-up imagery, relief, water and chunk continuity.</summary>
public sealed class RasterGroundTests
{
    static readonly WorldMapStyle Style = new() { ChunkMetres = 8f, PixelsPerMetre = 4f };

    /// <summary>Flat material channels reach the image without colour-space conversion.</summary>
    [Fact]
    public void FlatGround_EveryPixelIsTheGroundColour()
    {
        var source = Filled(Color.FromHex("#808040"));
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style, new(0, 0));
        Assert.Equal(new WorldMapChunkCoord(0, 0), image.Coord);
        Assert.Equal(32, image.Size);
        Assert.Equal(32 * 32 * 4, image.Rgba.Length);
        for (int row = 0; row < image.Size; row++)
            for (int column = 0; column < image.Size; column++)
                Assert.Equal(new byte[] { 128, 128, 64, 255 }, Pixel(image, column, row));
    }

    /// <summary>Source south-first rows become image north-first rows without mirroring columns.</summary>
    [Fact]
    public void Orientation_RowZeroIsNorth_ColumnZeroIsWest()
    {
        var source = new TestWorldMapSource(new MapRect(0f, 0f, 16f, 16f));
        for (int row = 0; row < 8; row++)
            for (int column = 0; column < 8; column++)
                source.SetCell(column, row, Color.FromHex("#00ff00"), 0f);
        source.SetCell(0, 7, Color.FromHex("#ff0000"), 0f);
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style, new(0, 0));
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(image, 0, 0));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(image, 0, 31));
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, Pixel(image, 31, 0));
    }

    /// <summary>Cell-centre interpolation crosses a material boundary monotonically.</summary>
    [Fact]
    public void Blend_AcrossMaterialEdge_IsMonotonic()
    {
        var source = Filled(Color.Black);
        for (int row = 0; row < 16; row++)
            for (int column = 4; column < 16; column++)
                source.SetCell(column, row, Color.White, 0f);
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style, new(0, 0));
        for (int column = 1; column < image.Size; column++)
            Assert.True(Pixel(image, column, 16)[0] >= Pixel(image, column - 1, 16)[0]);
        Assert.Equal(96, Pixel(image, 15, 16)[0]);
        Assert.Equal(159, Pixel(image, 16, 16)[0]);
        Assert.InRange((Pixel(image, 15, 16)[0] + Pixel(image, 16, 16)[0]) / 2f, 126f, 130f);
    }

    /// <summary>The north-west light brightens a facing slope and darkens its opposite.</summary>
    [Fact]
    public void Relief_NorthWestFacingSlope_BrighterThanFlat_SouthEastFacingDarker()
    {
        var northWest = Filled(Color.FromHex("#808080"), (column, row) => 0.3f * (column - row));
        var southEast = Filled(Color.FromHex("#808080"), (column, row) => 0.3f * (row - column));
        Assert.True(Pixel(WorldMapRasterizer.Rasterize(northWest, Style, new(0, 0)), 16, 16)[0] > 128);
        Assert.True(Pixel(WorldMapRasterizer.Rasterize(southEast, Style, new(0, 0)), 16, 16)[0] < 128);
    }

    /// <summary>Steep darkening multiplies the normalized relief factor.</summary>
    [Fact]
    public void Relief_SteepSlope_AppliesSteepDarken()
    {
        var source = Filled(Color.FromHex("#808080"), (column, row) => 1.4142f * (column + row));
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style, new(0, 0));
        float lit = 1f / MathF.Sqrt(5f);
        int expected = (int)MathF.Round(255f * (128f / 255f) * (1f + 0.5f * (lit - 1f)) * 0.75f);
        Assert.InRange((int)Pixel(image, 16, 16)[0], expected - 1, expected + 1);
    }

    /// <summary>Depth saturates at the deep palette and shallow water stays near its shallow colour.</summary>
    [Fact]
    public void Water_DeepIsDeepColour_ShallowIsCloserToShallowColour()
    {
        var deep = Filled(Color.Black, (_, _) => -10f, (_, _) => 0f);
        var shallow = Filled(Color.Black, (_, _) => -0.5f, (_, _) => 0f);
        byte[] deepPixel = Pixel(WorldMapRasterizer.Rasterize(deep, Style, new(0, 0)), 16, 16);
        byte[] shallowPixel = Pixel(WorldMapRasterizer.Rasterize(shallow, Style, new(0, 0)), 16, 16);
        Assert.Equal(new byte[] { 42, 90, 154, 255 }, deepPixel);
        Assert.Equal(new byte[] { 102, 148, 187, 255 }, shallowPixel);
        Assert.True(Distance(shallowPixel, [111, 156, 192, 255]) < Distance(shallowPixel, deepPixel));
    }

    /// <summary>Only wet pixels within the configured distance of dry land become shoreline.</summary>
    [Fact]
    public void Water_ShorelineOnWetPixelsBesideDry()
    {
        var source = Filled(Color.Black, (_, _) => -10f, (column, _) => column < 4 ? 0f : float.NaN);
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style, new(0, 0));
        Assert.Equal(new byte[] { 31, 63, 102, 255 }, Pixel(image, 15, 16));
        Assert.Equal(new byte[] { 42, 90, 154, 255 }, Pixel(image, 7, 16));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, Pixel(image, 16, 16));
    }

    /// <summary>Void cells keep their exact colour and do not introduce NaN into neighbouring relief.</summary>
    [Fact]
    public void Void_IsExactVoidColour_AndNeighbourReliefStaysFinite()
    {
        var source = new TestWorldMapSource(new MapRect(0f, 0f, 16f, 16f));
        for (int row = 0; row < 16; row++)
            for (int column = 4; column < 16; column++)
                source.SetCell(column, row, Color.FromHex("#808080"), 0.3f * (column - row));
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style, new(0, 0));
        for (int row = 0; row < 32; row++)
            for (int column = 0; column < 32; column++)
            {
                byte[] pixel = Pixel(image, column, row);
                if (column < 16) Assert.Equal(new byte[] { 217, 203, 166, 255 }, pixel);
                else
                {
                    Assert.True(pixel[0] > 0);
                    Assert.True(pixel[1] > 0);
                    Assert.True(pixel[2] > 0);
                }
            }
    }

    /// <summary>Relief and shoreline sampling across edges match a single larger raster byte for byte.</summary>
    [Fact]
    public void ChunkBorders_MatchOneLargeRaster()
    {
        var source = Varied();
        WorldMapChunkImage large = WorldMapRasterizer.Rasterize(source, Style with { ChunkMetres = 16f }, new(0, 0));
        for (int chunkY = 0; chunkY < 2; chunkY++)
            for (int chunkX = 0; chunkX < 2; chunkX++)
            {
                WorldMapChunkImage small = WorldMapRasterizer.Rasterize(source, Style, new(chunkX, chunkY));
                for (int row = 0; row < 32; row++)
                    for (int column = 0; column < 32; column++)
                        Assert.Equal(Pixel(large, chunkX * 32 + column, (1 - chunkY) * 32 + row),
                            Pixel(small, column, row));
            }
    }

    /// <summary>Repeated reads of one revision and style produce the same bytes and revision.</summary>
    [Fact]
    public void Rasterize_IsDeterministic()
    {
        var source = Varied();
        source.Revision = 37;
        WorldMapChunkImage first = WorldMapRasterizer.Rasterize(source, Style, new(0, 0));
        WorldMapChunkImage second = WorldMapRasterizer.Rasterize(source, Style, new(0, 0));
        Assert.Equal(SHA256.HashData(first.Rgba), SHA256.HashData(second.Rgba));
        Assert.Equal(37, first.Revision);
        Assert.Equal(37, second.Revision);
    }

    /// <summary>The one source read includes both relief cells and the configured shoreline radius.</summary>
    [Fact]
    public void Rasterize_SamplesOnce_WithReliefAndShorelineMargin()
    {
        var source = new TestWorldMapSource(new MapRect(-16f, -16f, 32f, 32f), 0.5f);
        MapRect? sampled = null;
        source.BeforeSample = area => sampled = area;
        WorldMapRasterizer.Rasterize(source, Style with { Water = Style.Water with { ShorelinePixels = 3 } }, new(-1, -1));
        Assert.Equal(1, source.SampleCalls);
        Assert.Equal(new MapRect(-9.75f, -9.75f, 11.5f, 11.5f), sampled);
    }

    /// <summary>A revision captured before a source read remains detectable as stale if sampling changes it.</summary>
    [Fact]
    public void Rasterize_RevisionChangingDuringSample_KeepsStartingRevision()
    {
        var source = Filled(Color.White);
        source.Revision = 5;
        source.BeforeSample = _ => source.Revision = 6;
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style, new(0, 0));
        Assert.Equal(5, image.Revision);
        Assert.Equal(6, source.Revision);
    }

    /// <summary>Missing surface beyond the source bounds cannot bleed colour into a void pixel.</summary>
    [Fact]
    public void Rasterize_OutsideSourceBounds_IsVoid()
    {
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(Filled(Color.White), Style, new(-1, -1));
        for (int row = 0; row < 32; row++)
            for (int column = 0; column < 32; column++)
                Assert.Equal(new byte[] { 217, 203, 166, 255 }, Pixel(image, column, row));
    }

    /// <summary>Void beside water is not dry land and therefore cannot paint a shoreline.</summary>
    [Fact]
    public void Water_VoidNeighbour_DoesNotCreateShoreline()
    {
        var source = new TestWorldMapSource(new MapRect(0f, 0f, 16f, 16f));
        for (int row = 0; row < 16; row++)
            for (int column = 0; column < 4; column++)
                source.SetCell(column, row, Color.White, -10f, 0f);
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, Style, new(0, 0));
        Assert.Equal(new byte[] { 42, 90, 154, 255 }, Pixel(image, 15, 16));
        Assert.Equal(new byte[] { 217, 203, 166, 255 }, Pixel(image, 16, 16));
    }

    /// <summary>Water height is renormalized over wet cells, rather than reduced by dry neighbours.</summary>
    [Fact]
    public void Water_SurfaceBlend_IgnoresDryCells()
    {
        var source = Filled(Color.Black, (_, _) => 0f, (column, _) => column < 4 ? 2f : float.NaN);
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source,
            Style with { Water = Style.Water with { ShorelinePixels = 0 } }, new(0, 0));
        Assert.Equal(new byte[] { 76, 123, 173, 255 }, Pixel(image, 15, 16));
    }

    /// <summary>Shoreline reaches diagonal dry pixels outside both chunk edges using Chebyshev distance.</summary>
    [Fact]
    public void Water_DiagonalDryPixelAcrossChunkEdges_UsesConfiguredRadius()
    {
        var source = Filled(Color.Black, (_, _) => -10f,
            (column, row) => column >= 8 && row >= 8 ? float.NaN : 0f);
        WorldMapChunkImage narrow = WorldMapRasterizer.Rasterize(source, Style, new(0, 0));
        WorldMapChunkImage wide = WorldMapRasterizer.Rasterize(source,
            Style with { Water = Style.Water with { ShorelinePixels = 2 } }, new(0, 0));
        Assert.Equal(new byte[] { 42, 90, 154, 255 }, Pixel(narrow, 31, 0));
        Assert.Equal(new byte[] { 31, 63, 102, 255 }, Pixel(wide, 31, 0));
    }

    /// <summary>A half-wet pixel is water even when its nearest cell is dry.</summary>
    [Fact]
    public void Water_ExactlyHalfWet_IsWater()
    {
        var source = Filled(Color.Black, (_, _) => 0f, (column, _) => column < 4 ? 2f : float.NaN);
        var style = Style with { PixelsPerMetre = 0.125f, Water = Style.Water with { ShorelinePixels = 0 } };
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, style, new(0, 0));
        Assert.Equal(1, image.Size);
        Assert.Equal(new byte[] { 76, 123, 173, 255 }, image.Rgba);
    }

    /// <summary>Canvas map coordinates use pixel centres, with north at row zero.</summary>
    [Fact]
    public void Canvas_MapPoint_UsesPixelCentresAndNorthUp()
    {
        var canvas = new RasterCanvas(32, new MapRect(-8f, -16f, 8f, 8f), 4f);
        Assert.Equal(new Vector2(-7.875f, -8.125f), canvas.MapPoint(0f, 0f));
        Assert.Equal(new Vector2(-0.125f, -15.875f), canvas.MapPoint(31f, 31f));
        Assert.Equal(new Vector2(-8.125f, -7.875f), canvas.MapPoint(-1f, -1f));
    }

    /// <summary>Coverage and colour alpha both control blending, and final bytes are clamped.</summary>
    [Fact]
    public void Canvas_Blend_UsesCoverageAndAlpha_ClipsOutsidePixels()
    {
        var canvas = new RasterCanvas(1, new MapRect(0f, 0f, 1f, 1f), 1f);
        canvas.Set(0, 0, Color.Black);
        canvas.Blend(0, 0, new Color(1f, 0.5f, 0f, 0.5f), 0.5f);
        canvas.Blend(-1, 0, Color.White, 1f);
        canvas.Blend(0, 1, Color.White, 1f);
        Assert.Equal(new byte[] { 64, 32, 0, 255 }, canvas.ToBytes());
        canvas.Set(0, 0, new Color(2f, -1f, 0.5f));
        Assert.Equal(new byte[] { 255, 0, 128, 255 }, canvas.ToBytes());
    }

    static TestWorldMapSource Filled(Color ground, Func<int, int, float>? height = null, Func<int, int, float>? water = null)
    {
        var source = new TestWorldMapSource(new MapRect(0f, 0f, 16f, 16f));
        for (int row = 0; row < 16; row++)
            for (int column = 0; column < 16; column++)
                source.SetCell(column, row, ground, height?.Invoke(column, row) ?? 0f,
                    water?.Invoke(column, row) ?? float.NaN);
        return source;
    }

    static TestWorldMapSource Varied()
    {
        var source = new TestWorldMapSource(new MapRect(0f, 0f, 16f, 16f));
        for (int row = 0; row < 16; row++)
            for (int column = 0; column < 16; column++)
            {
                if (column == 10 && row >= 10) continue;
                bool wet = column >= 4 && column < 8 && row >= 5 && row < 11;
                source.SetCell(column, row, Color.FromBytes((byte)(column * 13), (byte)(row * 11), 80),
                    0.3f * (column - row) + 0.2f * MathF.Sin(column + row), wet ? 4f : float.NaN);
            }
        return source;
    }

    static byte[] Pixel(WorldMapChunkImage image, int column, int row) =>
        image.Rgba.AsSpan((row * image.Size + column) * 4, 4).ToArray();

    static int Distance(byte[] a, byte[] b) =>
        Math.Abs(a[0] - b[0]) + Math.Abs(a[1] - b[1]) + Math.Abs(a[2] - b[2]);
}
