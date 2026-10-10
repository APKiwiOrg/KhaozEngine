using System.Collections.Generic;
using System.IO;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

// Each fixture goes through export load, sheet assembly, PNG output and reload. The frame and every sheet
// row must keep the straight-alpha RGBA8 pixels the fixture encodes.
public class PngCompatibilityTests
{
    private static readonly byte[] PaletteRgb = { 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110, 120 };

    [Fact]
    public void Palette_frames_load_as_opaque_rgba()
    {
        byte[] indices = { 0, 1, 2, 3, 2, 1 };
        byte[] png = ImageFixtures.Palette(3, 2, 8, indices, PaletteRgb, alpha: null);

        AssertSheetPreserves(png, 3, 2, ExpandPalette(indices, alpha: null));
    }

    [Fact]
    public void Palette_frames_with_transparency_keep_their_alpha()
    {
        // 2-bit indices over a width that leaves the last byte of each row partly used. The tRNS chunk covers
        // three of four entries, so index 3 stays opaque.
        byte[] indices = { 0, 1, 2, 3, 1, 3, 2, 1, 0, 3 };
        byte[] alpha = { 0, 64, 128 };
        byte[] png = ImageFixtures.Palette(5, 2, 2, indices, PaletteRgb, alpha);

        AssertSheetPreserves(png, 5, 2, ExpandPalette(indices, alpha));
    }

    [Fact]
    public void Adam7_frames_load_in_raster_order()
    {
        // 9x9 makes every one of the seven passes non-empty.
        const int size = 9;
        var rgb = new byte[size * size * 3];
        var rgba = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            rgb[i * 3] = rgba[i * 4] = (byte)(i * 3);
            rgb[i * 3 + 1] = rgba[i * 4 + 1] = (byte)(255 - i);
            rgb[i * 3 + 2] = rgba[i * 4 + 2] = (byte)(i * 7 + 11);
            rgba[i * 4 + 3] = 255;
        }

        AssertSheetPreserves(ImageFixtures.Adam7(size, size, 2, rgb), size, size, rgba);
    }

    [Fact]
    public void Adam7_rgba_frames_keep_straight_alpha()
    {
        // 3x2 leaves passes two and three empty. The transparent pixel keeps its colour.
        byte[] rgba =
        {
            10, 20, 30, 0, 40, 50, 60, 64, 70, 80, 90, 128,
            100, 110, 120, 255, 130, 140, 150, 1, 160, 170, 180, 200,
        };

        AssertSheetPreserves(ImageFixtures.Adam7(3, 2, 6, rgba), 3, 2, rgba);
    }

    private static byte[] ExpandPalette(byte[] indices, byte[]? alpha)
    {
        var rgba = new byte[indices.Length * 4];
        for (int i = 0; i < indices.Length; i++)
        {
            int entry = indices[i];
            rgba[i * 4] = PaletteRgb[entry * 3];
            rgba[i * 4 + 1] = PaletteRgb[entry * 3 + 1];
            rgba[i * 4 + 2] = PaletteRgb[entry * 3 + 2];
            rgba[i * 4 + 3] = alpha is not null && entry < alpha.Length ? alpha[entry] : (byte)255;
        }
        return rgba;
    }

    // Every fixture's bottom row holds a visible pixel, so assembly places the frame unshifted in each row.
    private static void AssertSheetPreserves(byte[] png, int width, int height, byte[] expected)
    {
        string root = Directory.CreateTempSubdirectory("sheet_png_").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "frame_000.png"), png);
            var directions = new List<string>();
            foreach (string direction in DirectionRows.NameToRow.Keys)
                directions.Add($"\"{direction}\": [\"frame_000.png\"]");
            File.WriteAllText(Path.Combine(root, "metadata.json"),
                "{\"states\":[{\"character\":{\"name\":\"Fixture\"},\"frames\":{\"animations\":{\"swim\":{" +
                string.Join(",", directions) + "}}}}]}");

            var (animation, _) = PixelLabExport.Load(root, "swim");
            AssertPixels(animation.FramesByDir["south"][0].Image, 0, width, height, expected);

            var result = SheetAssembler.Assemble(animation, new AssemblyOptions());
            string output = Path.Combine(root, "sheet.png");
            result.Sheet.SaveAsPng(output);
            var decoded = RgbaImage.Load(output);
            Assert.Equal(width, decoded.Width);
            Assert.Equal(height * DirectionRows.RowCount, decoded.Height);
            for (int row = 0; row < DirectionRows.RowCount; row++)
                AssertPixels(decoded, row * height, width, height, expected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertPixels(RgbaImage image, int top, int width, int height, byte[] expected)
    {
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int o = (y * width + x) * 4;
                Assert.Equal(new Rgba32(expected[o], expected[o + 1], expected[o + 2], expected[o + 3]),
                    image[x, top + y]);
            }
    }
}
