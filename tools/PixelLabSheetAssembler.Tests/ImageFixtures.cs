using KhaozEngine.Imaging;
using KhaozEngine.Render2D;

namespace PixelLabSheetAssembler.Tests;

internal static class ImageFixtures
{
    internal static ImageRgba Create(int width, int height) =>
        new(new byte[checked(width * height * 4)], width, height);

    internal static void SetPixel(ImageRgba image, int x, int y, byte r, byte g, byte b, byte a)
    {
        int offset = (y * image.Width + x) * 4;
        image.Pixels[offset] = r;
        image.Pixels[offset + 1] = g;
        image.Pixels[offset + 2] = b;
        image.Pixels[offset + 3] = a;
    }

    internal static void Save(ImageRgba image, string path) =>
        PngWriter.Save(path, image.Pixels, image.Width, image.Height);
}
