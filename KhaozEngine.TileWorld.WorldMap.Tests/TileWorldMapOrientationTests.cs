using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.TileWorld;
using KhaozEngine.TileWorld.WorldMap;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.TileWorld.WorldMap;

/// <summary>Tile north remains map north through conversion and the real rasterizer.</summary>
public sealed class TileWorldMapOrientationTests
{
    /// <summary>World metres convert through the TileWorld seam without mirroring or rescaling.</summary>
    [Fact]
    public void ToMap_NorthIsPositiveY_EastIsPositiveX()
    {
        var source = new TileWorldMapSource(TileWorldMapSourceTests.FlatWorld(), TileWorldMapSourceTests.Catalogs());
        Assert.Equal(new Vector2(10.5f, 20.5f), source.ToMap(TileWorldSpace.ToWorld(10.5f, 0f, 20.5f, 1f)));
        var scaled = new TileWorldMapSource(TileWorldMapSourceTests.FlatWorld(2f), TileWorldMapSourceTests.Catalogs());
        Assert.Equal(new Vector2(-21, 41), scaled.ToMap(TileWorldSpace.ToWorld(-10.5f, 7f, 20.5f, 2f)));
    }

    /// <summary>Headings run clockwise from world minus Z, with zero forward remaining zero.</summary>
    [Fact]
    public void ToMapHeading_WorldMinusZIsNorth_PlusXIsEast()
    {
        var source = new TileWorldMapSource(TileWorldMapSourceTests.FlatWorld(), TileWorldMapSourceTests.Catalogs());
        Assert.Equal(0f, source.ToMapHeading(new Vector3(0, 0, -1)));
        Assert.Equal(MathF.PI / 2f, source.ToMapHeading(new Vector3(1, 0, 0)), 5);
        Assert.Equal(MathF.PI, MathF.Abs(source.ToMapHeading(new Vector3(0, 0, 1))), 5);
        Assert.Equal(-MathF.PI / 2f, source.ToMapHeading(new Vector3(-1, 0, 0)), 5);
        Assert.Equal(0f, source.ToMapHeading(Vector3.Zero));
    }

    /// <summary>Independently coloured object identities actually rasterize north up and east right.</summary>
    [Fact]
    public void Rasterized_ObjectFurtherNorthIsHigher_FurtherEastIsRighter()
    {
        TileWorldDocument doc = TileWorldMapSourceTests.FlatWorld();
        doc.AddObject("tree", 10, 40, 0, 0);
        doc.AddObject("hall", 30, 10, 0, 0);
        var source = new TileWorldMapSource(doc, TileWorldMapSourceTests.Catalogs());
        var red = Color.FromHex("#ff0000");
        var blue = Color.FromHex("#0000ff");
        var style = new WorldMapStyle
        {
            SymbolRules =
            [
                new(new(ArchetypeKey: "tree"), new(WorldMapSymbolKind.Obstacle, red, red)),
                new(new(ArchetypeKey: "hall"), new(WorldMapSymbolKind.Obstacle, blue, blue))
            ]
        };
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(source, style, new WorldMapChunkCoord(0, 0));
        var north = FindSymbolPixel(image, 255, 0, 0);
        var south = FindSymbolPixel(image, 0, 0, 255);
        Assert.True(north.Row < south.Row);
        Assert.True(north.Column < south.Column);
    }

    static (int Row, int Column) FindSymbolPixel(WorldMapChunkImage image, byte red, byte green, byte blue)
    {
        for (int row = 0; row < image.Size; row++)
            for (int column = 0; column < image.Size; column++)
            {
                int index = (row * image.Size + column) * 4;
                if (image.Rgba[index] == red && image.Rgba[index + 1] == green && image.Rgba[index + 2] == blue)
                    return (row, column);
            }
        Assert.Fail("No pixels found for the object's distinct symbol colour in the whole raster.");
        return default;
    }
}
