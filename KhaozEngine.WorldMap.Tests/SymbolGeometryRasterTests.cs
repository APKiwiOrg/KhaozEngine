using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Vector symbols use normalized silhouettes and constant raster pixel stroke widths.</summary>
public sealed class SymbolGeometryRasterTests
{
    internal static readonly Vector2[] Rectangle = [new(-1f), new(1f, -1f), new(1f), new(-1f, 1f)];
    internal static readonly Vector2[] Concave = [new(-1f), new(1f, -1f), new(1f, 0f), Vector2.Zero, new(0f, 1f), new(-1f, 1f)];
    static readonly byte[] Ground = [128, 128, 128, 255];
    static readonly byte[] White = [255, 255, 255, 255];
    static readonly byte[] Black = [0, 0, 0, 255];

    /// <summary>A concave cutout stays empty for both outline windings.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SymbolGeometry_ConcaveSilhouetteKeepsNotchEmpty(bool reverse)
    {
        var geometry = new WorldMapSymbolGeometry(reverse ? Concave.Reverse() : Concave);
        WorldMapChunkImage image = Render(geometry, new(2f));
        Assert.Equal(White, Pixel(image, 3.125f, 5.125f));
        Assert.Equal(White, Pixel(image, 5.125f, 3.125f));
        Assert.Equal(Ground, Pixel(image, 5.125f, 5.125f));
    }

    /// <summary>A quarter turn rotates an asymmetric non-square polygon clockwise with its footprint.</summary>
    [Fact]
    public void SymbolGeometry_NonSquareQuarterTurnRotatesSilhouette()
    {
        var geometry = new WorldMapSymbolGeometry([new(-1f), new(1f, -1f), new(-1f, 1f)]);
        WorldMapChunkImage image = Render(geometry, new(2f, 1f), rotation: MathF.PI / 2f);
        Assert.Equal(White, Pixel(image, 4.125f, 5.625f));
        Assert.Equal(Ground, Pixel(image, 4.625f, 2.625f));
        Assert.Equal(Ground, Pixel(image, 5.375f, 4.125f));
    }

    /// <summary>A sloping polygon boundary covers twelve of the fixed sixteen samples in this pixel.</summary>
    [Fact]
    public void SymbolGeometry_SlopedFillUsesFixedSupersampleCoverage()
    {
        var geometry = new WorldMapSymbolGeometry([new(-1f), new(1f, -1f), new(-1f, 1f)]);
        WorldMapChunkImage image = Render(geometry, new(2f, 1f));
        Assert.Equal(new byte[] { 223, 223, 223, 255 }, Pixel(image, 4.125f, 3.875f));
    }

    /// <summary>Inset edges keep one pixel of ink along both dimensions of a non-square polygon.</summary>
    [Theory]
    [InlineData(0f)]
    [InlineData(1.5707964f)]
    public void SymbolGeometry_NonSquareBoundaryWidthStaysInPixels(float rotation)
    {
        WorldMapChunkImage image = Render(new(Rectangle), new(2f, 1f), width: 1f, rotation: rotation);
        if (rotation == 0f)
        {
            Assert.Equal(Black, Pixel(image, 2.125f, 4.125f));
            Assert.Equal(White, Pixel(image, 2.375f, 4.125f));
            Assert.Equal(Black, Pixel(image, 4.125f, 4.875f));
            Assert.Equal(White, Pixel(image, 4.125f, 4.625f));
        }
        else
        {
            Assert.Equal(Black, Pixel(image, 3.125f, 4.125f));
            Assert.Equal(White, Pixel(image, 3.375f, 4.125f));
            Assert.Equal(Black, Pixel(image, 4.125f, 5.875f));
            Assert.Equal(White, Pixel(image, 4.125f, 5.625f));
        }
    }

    /// <summary>Independent crossing details share one outline contribution rather than darkening twice.</summary>
    [Fact]
    public void SymbolGeometry_RidgeIntersectionsUnionCoverage()
    {
        var geometry = new WorldMapSymbolGeometry(Rectangle,
            [new(new(-1f, 0f), new(1f, 0f)), new(new(0f, -1f), new(0f, 1f))]);
        WorldMapChunkImage image = Render(geometry, new(2f), width: 2f, outline: new(0f, 0f, 0f, 0.5f));
        byte[] ink = [64, 64, 64, 255];
        Assert.Equal(ink, Pixel(image, 4.125f, 4.125f));
        Assert.Equal(ink, Pixel(image, 4.125f, 5.125f));
        Assert.Equal(ink, Pixel(image, 2.125f, 4.125f));
        Assert.Equal(White, Pixel(image, 5.125f, 5.125f));
    }

    /// <summary>Horizontal and vertical detail segments keep the same width after non-square scaling.</summary>
    [Fact]
    public void SymbolGeometry_NonSquareDetailWidthsStayInPixels()
    {
        var geometry = new WorldMapSymbolGeometry(Rectangle,
            [new(new(-0.5f, 0f), new(0.5f, 0f)), new(new(0f, -0.5f), new(0f, 0.5f))]);
        WorldMapChunkImage image = Render(geometry, new(3f, 1.5f), width: 2f);
        Assert.Equal(Black, Pixel(image, 4.125f, 4.375f));
        Assert.Equal(White, Pixel(image, 4.375f, 4.375f));
        Assert.Equal(Black, Pixel(image, 5.125f, 4.125f));
        Assert.Equal(White, Pixel(image, 5.125f, 4.375f));
    }

    /// <summary>Transparent polygon fill preserves terrain while its boundary and external details draw.</summary>
    [Fact]
    public void SymbolGeometry_TransparentFillAndExternalDetailPreserveTerrain()
    {
        var geometry = new WorldMapSymbolGeometry(Concave, [new(new(0.5f, 0.25f), new(0.5f, 1f))]);
        WorldMapChunkImage image = Render(geometry, new(2f), width: 2f, fill: Color.Transparent);
        Assert.Equal(Ground, Pixel(image, 3.125f, 3.125f));
        Assert.Equal(Black, Pixel(image, 5.125f, 5.125f));
        Assert.Equal(Ground, Pixel(image, 5.625f, 5.125f));
        Assert.Equal(Black, Pixel(image, 2.125f, 3.125f));
        Assert.Equal(Ground, Pixel(image, 5.125f, 6.125f));
    }

    /// <summary>Building shadows fill the concave silhouette without projecting independent detail lines.</summary>
    [Fact]
    public void SymbolGeometry_BuildingShadowUsesOnlyFilledSilhouette()
    {
        var geometry = new WorldMapSymbolGeometry(Concave, [new(new(0.5f, 0.25f), new(0.5f, 1f))]);
        var symbol = new WorldMapSymbol(WorldMapSymbolKind.Building, Color.Transparent, Color.Transparent) { Geometry = geometry };
        var style = SymbolSizeTests.Style(symbol) with
        {
            OutlinePixels = 2f,
            BuildingShadow = Color.Black,
            BuildingShadowOffsetMetres = new(0f, 1f)
        };
        var source = SymbolSizeTests.Source();
        source.Footprints.Add(SymbolSizeTests.Footprint(new(2f)));
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, style, new(0, 0));
        Assert.Equal(Black, Pixel(image, 3.125f, 6.125f));
        Assert.Equal(Ground, Pixel(image, 5.125f, 6.125f));
    }

    /// <summary>Custom wall outlines use the configured ink width inside the existing wall dimensions.</summary>
    [Fact]
    public void SymbolGeometry_WallKeepsOutlineAndSizingRules()
    {
        var symbol = new WorldMapSymbol(WorldMapSymbolKind.Wall, Color.White, Color.Black) { Geometry = new(Rectangle) };
        var source = SymbolSizeTests.Source();
        source.Footprints.Add(SymbolSizeTests.Footprint(new(2f, 1f)));
        var style = SymbolSizeTests.Style(symbol) with { WallWidthMetres = 1f, OutlinePixels = 1f };
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, style, new(0, 0));
        Assert.Equal(Black, Pixel(image, 2.125f, 4.125f));
        Assert.Equal(White, Pixel(image, 3.125f, 4.125f));
        Assert.Equal(Ground, Pixel(image, 3.125f, 4.625f));
    }

    internal static WorldMapChunkImage Render(WorldMapSymbolGeometry geometry, Vector2 halfExtents,
        float width = 0f, float rotation = 0f, Color? fill = null, Color? outline = null)
    {
        var source = SymbolSizeTests.Source();
        source.Footprints.Add(SymbolSizeTests.Footprint(halfExtents, rotation));
        var symbol = new WorldMapSymbol(WorldMapSymbolKind.Tree, fill ?? Color.White, outline ?? Color.Black) { Geometry = geometry };
        return WorldMapRasterizer.Rasterize(source, SymbolSizeTests.Style(symbol) with { OutlinePixels = width }, new(0, 0));
    }

    static byte[] Pixel(WorldMapChunkImage image, float x, float y) => SymbolSizeTests.Pixel(image, new(x, y));
}
