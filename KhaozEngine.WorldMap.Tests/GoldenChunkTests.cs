using System;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using KhaozEngine.Imaging;
using KhaozEngine.Primitives;
using KhaozEngine.WorldMap;
using Xunit;

namespace KhaozEngine.Tests.WorldMap;

/// <summary>Pins a complete, north-up CPU raster fixture across terrain, water and footprints.</summary>
public sealed class GoldenChunkTests
{
    /// <summary>Compares every RGBA channel within two bytes, or explicitly bakes the committed reference.</summary>
    [Fact]
    public void FixtureChunk_MatchesGolden()
    {
        WorldMapChunkImage image = WorldMapRasterizer.Rasterize(Fixture(), Style(), new(0, 0));
        string path = GoldenPath();
        if (Environment.GetEnvironmentVariable("KE_UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, PngWriter.Encode(image.Rgba, image.Size, image.Size));
            return;
        }

        Assert.True(File.Exists(path), $"Golden missing at {path}. Bake with KE_UPDATE_GOLDENS=1.");
        PngImage golden = PngReader.Decode(File.ReadAllBytes(path));
        Assert.Equal(128, image.Size);
        Assert.Equal(image.Size, golden.Width);
        Assert.Equal(image.Size, golden.Height);
        Assert.Equal(4, golden.Channels);
        Assert.Equal(8, golden.BitDepth);
        Assert.Equal(image.Rgba.Length, golden.Bytes.Length);
        for (int channel = 0; channel < image.Rgba.Length; channel++)
            Assert.True(Math.Abs(image.Rgba[channel] - golden.Bytes[channel]) <= 2,
                $"Pixel ({channel / 4 % image.Size}, {channel / 4 / image.Size}), channel {channel % 4}: " +
                $"expected {golden.Bytes[channel]}, got {image.Rgba[channel]}.");
    }

    static WorldMapStyle Style() => new()
    {
        ChunkMetres = 32f,
        PixelsPerMetre = 4f,
        SymbolRules = [
            new(new(ArchetypeKey: "house"), new(WorldMapSymbolKind.Building, Color.FromHex("#b96c46"), Color.FromHex("#57392c"))),
            new(new(ArchetypeKey: "tree"), new(WorldMapSymbolKind.Tree, Color.FromHex("#477e40"), Color.FromHex("#254a29"))),
            new(new(ArchetypeKey: "wall"), new(WorldMapSymbolKind.Wall, Color.FromHex("#695e53"), Color.Black)),
            new(new(ArchetypeKey: "bridge"), new(WorldMapSymbolKind.Bridge, Color.FromHex("#bc9b6a"), Color.FromHex("#655039")))
        ]
    };

    static TestWorldMapSource Fixture()
    {
        var source = new TestWorldMapSource(new MapRect(0f, 0f, 32f, 32f), 0.5f);
        for (int row = 0; row < 64; row++)
            for (int column = 0; column < 64; column++)
            {
                float x = (column + 0.5f) * 0.5f;
                float y = (row + 0.5f) * 0.5f;
                float hill = 4f * MathF.Exp(-((x - 6f) * (x - 6f) + (y - 25f) * (y - 25f)) / 40f);
                float pondRadius = (x - 23f) * (x - 23f) / 25f + (y - 19f) * (y - 19f) / 16f;
                bool wet = pondRadius < 1f;
                Color ground = Color.FromHex(x < 18f ? "#91a86b" : "#c4ad7d");
                source.SetCell(column, row, ground, wet ? -0.4f - 3.6f * (1f - pondRadius) : hill,
                    wet ? 0f : float.NaN);
            }

        source.Footprints.Add(new(1, "house", new(8f, 9f), new(3f, 2f), 0f, [], true, WorldMapCollision.Solid));
        source.Footprints.Add(new(2, "house", new(14f, 25f), new(3f, 1.5f), MathF.PI / 6f, [], true, WorldMapCollision.Solid));
        source.Footprints.Add(new(3, "tree", new(5f, 23f), new(1.5f), 0f, [], false, WorldMapCollision.Solid));
        source.Footprints.Add(new(4, "tree", new(9f, 25f), new(1.25f), 0f, [], false, WorldMapCollision.Solid));
        source.Footprints.Add(new(5, "tree", new(10f, 10f), new(1.5f), 0f, [], false, WorldMapCollision.Solid));
        source.Footprints.Add(new(6, "tree", new(13.5f, 25f), new(1.25f), 0f, [], false, WorldMapCollision.Solid));
        source.Footprints.Add(new(7, "wall", new(8f, 16f), new(4f, 0.5f), 0f, [], false, WorldMapCollision.Wall));
        source.Footprints.Add(new(8, "bridge", new(23f, 19f), new(6f, 0.75f), 0f, [], false, WorldMapCollision.None));
        return source;
    }

    static string GoldenPath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden", "worldmap-fixture.png");
}
