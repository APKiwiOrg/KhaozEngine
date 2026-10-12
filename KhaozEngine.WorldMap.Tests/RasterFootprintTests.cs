using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Proves footprint geometry, compositing order, coverage and chunk continuity.</summary>
public sealed class RasterFootprintTests
{
    static readonly byte[] Ground = [128, 128, 128, 255];
    static readonly WorldMapStyle Style = new()
    {
        ChunkMetres = 8f,
        PixelsPerMetre = 4f,
        SymbolRules = Enum.GetValues<WorldMapSymbolKind>()
            .Select(kind => new WorldMapSymbolRule(new(ArchetypeKey: kind.ToString()), Symbol(kind))).ToArray()
    };

    /// <summary>A quarter turn exchanges the building's horizontal and vertical spans.</summary>
    [Fact]
    public void Building_QuarterTurn_SwapsExtents()
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Building, new(2f, 1f), rotation: MathF.PI / 2f));
        WorldMapChunkImage image = Rasterize(source);
        int minColumn = 32, maxColumn = -1, minRow = 32, maxRow = -1;
        for (int row = 0; row < image.Size; row++)
            for (int column = 0; column < image.Size; column++)
            {
                byte[] pixel = Pixel(image, column, row);
                if (!pixel.SequenceEqual(new byte[] { 224, 80, 48, 255 }) &&
                    !pixel.SequenceEqual(new byte[] { 80, 16, 0, 255 })) continue;
                minColumn = Math.Min(minColumn, column);
                maxColumn = Math.Max(maxColumn, column);
                minRow = Math.Min(minRow, row);
                maxRow = Math.Max(maxRow, row);
            }
        Assert.Equal(8, maxColumn - minColumn + 1);
        Assert.Equal(16, maxRow - minRow + 1);
    }

    /// <summary>The outline stays inside and the default shadow lies east and south only.</summary>
    [Fact]
    public void Building_OutlineInside_ShadowSouthEast_NoneNorthWest()
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Building, new(2f, 1f)));
        WorldMapChunkImage image = Rasterize(source);
        Assert.Equal(new byte[] { 80, 16, 0, 255 }, Pixel(image, 8, 16));
        Assert.Equal(new byte[] { 224, 80, 48, 255 }, Pixel(image, 16, 16));
        Assert.All(Pixel(image, 24, 20).Take(3), channel => Assert.True(channel < 128));
        Assert.Equal(Ground, Pixel(image, 7, 11));
    }

    /// <summary>A square tree footprint paints an inscribed disc rather than its bounding corners.</summary>
    [Fact]
    public void Tree_IsDisc_FootprintCornersStayGround()
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Tree, new(1f)));
        WorldMapChunkImage image = Rasterize(source);
        Assert.Equal(new byte[] { 32, 192, 48, 255 }, Pixel(image, 16, 16));
        foreach ((int column, int row) in new[] { (12, 12), (19, 12), (12, 19), (19, 19) })
            Assert.Equal(Ground, Pixel(image, column, row));
    }

    /// <summary>Explicitly hidden blocking footprints do not draw a fallback or a shadow.</summary>
    [Fact]
    public void Hidden_LeavesGroundUntouched()
    {
        var source = Filled();
        WorldMapChunkImage empty = Rasterize(source);
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Hidden, new(2f)));
        Assert.Equal(empty.Rgba, Rasterize(source).Rgba);
    }

    /// <summary>Kind order takes precedence over source collection order and identifiers.</summary>
    [Fact]
    public void DrawOrder_TreeOverBuildingOverObstacle()
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Tree, new(1f), id: 1));
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Building, new(2f), id: 2));
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Obstacle, new(3f), id: 3));
        Assert.Equal(new byte[] { 32, 192, 48, 255 }, Pixel(Rasterize(source), 16, 16));
        source.Footprints.RemoveAt(0);
        Assert.Equal(new byte[] { 224, 80, 48, 255 }, Pixel(Rasterize(source), 16, 16));
        source.Footprints.RemoveAt(0);
        Assert.Equal(new byte[] { 128, 48, 208, 255 }, Pixel(Rasterize(source), 16, 16));
    }

    /// <summary>The wall uses the configured narrow width, not the footprint's short-axis width.</summary>
    [Fact]
    public void Wall_IsLineAlongLongAxis()
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Wall, new(2f, 0.5f), new(4.125f, 3.875f)));
        WorldMapChunkImage image = Rasterize(source);
        Assert.Equal(new byte[] { 240, 208, 32, 255 }, Pixel(image, 16, 16));
        Assert.Equal(Ground, Pixel(image, 16, 14));
    }

    /// <summary>A border-straddling rotated building and its shadow match a larger raster exactly.</summary>
    [Fact]
    public void Footprint_StraddlingChunkBorder_HasNoSeam()
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Building, new(2f, 1f), new(8f, 4f), MathF.PI / 6f));
        WorldMapChunkImage large = Rasterize(source, Style with { ChunkMetres = 16f });
        for (int chunkX = 0; chunkX < 2; chunkX++)
        {
            WorldMapChunkImage small = WorldMapRasterizer.Rasterize(source, Style, new(chunkX, 0));
            for (int row = 0; row < 32; row++)
                for (int column = 0; column < 32; column++)
                    Assert.Equal(Pixel(large, chunkX * 32 + column, 32 + row), Pixel(small, column, row));
        }
    }

    /// <summary>A widened wall reaches the neighbouring chunk even when its authored footprint does not.</summary>
    [Fact]
    public void Wall_WiderThanFootprint_HasNoChunkSeam()
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Wall, new(2f, 0.05f), new(4f, 7.6f)));
        var style = Style with { BuildingShadowOffsetMetres = Vector2.Zero, WallWidthMetres = 2f };
        WorldMapChunkImage north = WorldMapRasterizer.Rasterize(source, style, new(0, 1));
        Assert.Equal(new byte[] { 240, 208, 32, 255 }, Pixel(north, 16, 31));

        WorldMapChunkImage large = Rasterize(source, style with { ChunkMetres = 16f });
        for (int chunkY = 0; chunkY < 2; chunkY++)
        {
            WorldMapChunkImage small = WorldMapRasterizer.Rasterize(source, style, new(0, chunkY));
            for (int row = 0; row < 32; row++)
                for (int column = 0; column < 32; column++)
                    Assert.Equal(Pixel(large, column, (1 - chunkY) * 32 + row), Pixel(small, column, row));
        }
    }

    /// <summary>Positive rotation turns the long axis clockwise towards the south-east.</summary>
    [Fact]
    public void Building_PositiveRotation_IsClockwise()
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Building, new(2f, 0.5f), rotation: MathF.PI / 4f));
        WorldMapChunkImage image = Rasterize(source, Style with { BuildingShadow = Color.Transparent });
        Assert.Equal(new byte[] { 224, 80, 48, 255 }, Pixel(image, 20, 20));
        Assert.Equal(Ground, Pixel(image, 20, 11));
    }

    /// <summary>Four by four subsamples combine three-quarter coverage and colour alpha without changing output alpha.</summary>
    [Fact]
    public void Coverage_EdgeBlendsWithColourAlpha()
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Building, new(1f), new(4.0625f)));
        var style = Style with
        {
            OutlinePixels = 0f,
            BuildingShadow = Color.Transparent,
            SymbolRules = [new(new(), new(WorldMapSymbolKind.Building, new Color(1f, 1f, 1f, 0.5f), Color.Black))]
        };
        Assert.Equal(new byte[] { 176, 176, 176, 255 }, Pixel(Rasterize(source, style), 12, 16));
        Assert.Equal(new byte[] { 164, 164, 164, 255 }, Pixel(Rasterize(source, style), 12, 19));
    }

    /// <summary>Disjoint opaque fill and outline samples fully replace the background in a covered pixel.</summary>
    [Fact]
    public void Building_MixedFillAndOutline_IsIndependentOfGround()
    {
        var style = Style with
        {
            BuildingShadow = Color.Transparent,
            BuildingShadowOffsetMetres = Vector2.Zero,
            OutlinePixels = 1f,
            SymbolRules = [new(new(), new(WorldMapSymbolKind.Building, Color.White, Color.Black))]
        };
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Building, new(1f), new(4.125f)));
        byte[] greyGroundPixel = Pixel(Rasterize(source, style), 13, 16);
        Assert.All(greyGroundPixel.Take(3), channel => Assert.InRange((int)channel, 127, 129));
        Assert.Equal((byte)255, greyGroundPixel[3]);

        var darkSource = Filled(Color.FromHex("#202020"));
        darkSource.Footprints.Add(source.Footprints[0]);
        Assert.Equal(greyGroundPixel, Pixel(Rasterize(darkSource, style), 13, 16));
    }

    /// <summary>Disjoint fill and outline samples each retain their own colour alpha.</summary>
    [Theory]
    [InlineData(0.5f, 0.25f, 144)]
    [InlineData(0.25f, 0.5f, 112)]
    [InlineData(0f, 1f, 64)]
    [InlineData(1f, 0f, 192)]
    [InlineData(0f, 0f, 128)]
    public void Building_MixedFillAndOutline_HonoursEachAlpha(float fillAlpha, float outlineAlpha, int expected)
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Building, new(1f), new(4.125f)));
        var style = Style with
        {
            BuildingShadow = Color.Transparent,
            OutlinePixels = 1f,
            SymbolRules = [new(new(), new(WorldMapSymbolKind.Building,
                new Color(1f, 1f, 1f, fillAlpha), new Color(0f, 0f, 0f, outlineAlpha)))]
        };
        byte[] pixel = Pixel(Rasterize(source, style), 13, 16);
        Assert.All(pixel.Take(3), channel => Assert.InRange((int)channel, expected - 1, expected + 1));
        Assert.Equal((byte)255, pixel[3]);
    }

    /// <summary>Ascending identifiers determine which same-kind fill appears on top.</summary>
    [Fact]
    public void DrawOrder_WithinKind_IsAscendingId()
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Building, new(2f), id: 9) with { ArchetypeKey = "high" });
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Building, new(2f), id: 2) with { ArchetypeKey = "low" });
        var style = Style with
        {
            SymbolRules = [new(new(ArchetypeKey: "high"), new(WorldMapSymbolKind.Building, Color.White, Color.Black)),
                new(new(ArchetypeKey: "low"), new(WorldMapSymbolKind.Building, Color.Black, Color.White))]
        };
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, Pixel(Rasterize(source, style), 16, 16));
    }

    /// <summary>All building shadows precede bridge fills, including shadows cast from outside the chunk.</summary>
    [Fact]
    public void BuildingShadow_FromOutsideChunk_IsUnderBridge()
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Building, new(0.5f, 1f), new(-0.6f, 4f)));
        Assert.All(Pixel(Rasterize(source), 0, 16).Take(3), channel => Assert.True(channel < 128));
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Bridge, new(0.5f), new(0.125f, 3.875f)));
        Assert.Equal(new byte[] { 32, 96, 224, 255 }, Pixel(Rasterize(source), 0, 16));
    }

    /// <summary>Walls follow the longer local axis and use local x when the extents tie.</summary>
    [Theory]
    [InlineData(0.5f, 2f, 16, 12, 18, 16)]
    [InlineData(2f, 2f, 12, 16, 16, 12)]
    public void Wall_LongAxisAndTie(float halfX, float halfY, int fillX, int fillY, int groundX, int groundY)
    {
        var source = Filled();
        source.Footprints.Add(Footprint(WorldMapSymbolKind.Wall, new(halfX, halfY), new(4.125f, 3.875f)));
        WorldMapChunkImage image = Rasterize(source);
        Assert.Equal(new byte[] { 240, 208, 32, 255 }, Pixel(image, fillX, fillY));
        Assert.Equal(Ground, Pixel(image, groundX, groundY));
    }

    static WorldMapFootprint Footprint(WorldMapSymbolKind kind, Vector2 halfExtents, Vector2? center = null,
        float rotation = 0f, long id = 1) => new(id, kind.ToString(), center ?? new Vector2(4f), halfExtents,
            rotation, [], kind == WorldMapSymbolKind.Building, WorldMapCollision.Solid);

    static WorldMapSymbol Symbol(WorldMapSymbolKind kind) => kind switch
    {
        WorldMapSymbolKind.Bridge => new(kind, Color.FromHex("#2060e0"), Color.FromHex("#002050")),
        WorldMapSymbolKind.Obstacle => new(kind, Color.FromHex("#8030d0"), Color.FromHex("#300050")),
        WorldMapSymbolKind.Wall => new(kind, Color.FromHex("#f0d020"), Color.FromHex("#605000")),
        WorldMapSymbolKind.Building => new(kind, Color.FromHex("#e05030"), Color.FromHex("#501000")),
        WorldMapSymbolKind.Tree => new(kind, Color.FromHex("#20c030"), Color.FromHex("#004010")),
        _ => WorldMapSymbol.Hidden
    };

    static TestWorldMapSource Filled(Color? ground = null)
    {
        var source = new TestWorldMapSource(new MapRect(-4f, -4f, 24f, 24f));
        for (int row = 0; row < 24; row++)
            for (int column = 0; column < 24; column++)
                source.SetCell(column, row, ground ?? Color.FromHex("#808080"), 0f);
        return source;
    }

    static WorldMapChunkImage Rasterize(TestWorldMapSource source, WorldMapStyle? style = null) =>
        WorldMapRasterizer.Rasterize(source, style ?? Style, new(0, 0));

    static byte[] Pixel(WorldMapChunkImage image, int column, int row) =>
        image.Rgba.AsSpan((row * image.Size + column) * 4, 4).ToArray();
}
