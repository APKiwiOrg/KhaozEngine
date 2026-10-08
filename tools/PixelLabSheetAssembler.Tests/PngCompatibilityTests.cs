using System;
using System.Collections.Generic;
using System.IO;
using KhaozEngine.Imaging;
using KhaozEngine.Render2D;
using Xunit;

namespace PixelLabSheetAssembler.Tests;

public class PngCompatibilityTests
{
    // Hand-encoded 2x2 fixtures. Palette+tRNS and Adam7 RGBA encode the same straight-alpha pixels.
    [Theory]
    [InlineData("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAMAAABFaP0WAAAADFBMVEUKFB4oMjxGUFpkbnjGSHffAAAABHRSTlMAQID/1YicnAAAAA5JREFUeJxjYGBkYGIGAAARAAeeoioSAAAAAElFTkSuQmCC")]
    [InlineData("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAAEFsT2yAAAAG0lEQVR4nGPgEpFjYNAwsnFgcAuIakjJq/gPABs0BMyHE2GhAAAAAElFTkSuQmCC")]
    public void ExportAssemblyAndPngOutputPreserveEncodedPixels(string encoded)
    {
        string root = Directory.CreateTempSubdirectory("sheet_png_").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "frame_000.png"), Convert.FromBase64String(encoded));
            var directions = new List<string>();
            foreach (string direction in DirectionRows.NameToRow.Keys)
                directions.Add($"\"{direction}\": [\"frame_000.png\"]");
            File.WriteAllText(Path.Combine(root, "metadata.json"),
                "{\"states\":[{\"character\":{\"name\":\"Fixture\"},\"frames\":{\"animations\":{\"swim\":{" +
                string.Join(",", directions) + "}}}}]}");
            var (animation, _) = PixelLabExport.Load(root, "swim");
            var result = SheetAssembler.Assemble(animation, new AssemblyOptions());
            byte[] expected = [10, 20, 30, 0, 40, 50, 60, 64, 70, 80, 90, 128, 100, 110, 120, 255];
            Assert.Equal(expected, animation.FramesByDir["south"][0].Image.Pixels);
            string output = Path.Combine(root, "sheet.png");
            PngWriter.Save(output, result.Sheet.Pixels, result.Sheet.Width, result.Sheet.Height);
            var decoded = ImageRgba.Load(output);
            Assert.Equal(2, decoded.Width);
            Assert.Equal(16, decoded.Height);
            for (int row = 0; row < DirectionRows.RowCount; row++)
                Assert.Equal(expected, decoded.Pixels.AsSpan(row * expected.Length, expected.Length).ToArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
