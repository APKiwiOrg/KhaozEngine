using System;

namespace KhaozEngine.Imaging;

/// <summary>
/// Reconstructs the raster from a PNG's filtered scanlines, for both the single noninterlaced pass and the seven
/// Adam7 passes. Each pass is unfiltered on its own, because the Up, Average and Paeth predictors restart at the
/// first row of every pass. The result holds one whole pixel per slot in raster order. Indices packed below eight
/// bits are widened to one byte each.
/// </summary>
internal static class PngInterlace
{
    private readonly record struct Pass(int X, int Y, int Dx, int Dy);

    private static readonly Pass[] Single = { new(0, 0, 1, 1) };

    private static readonly Pass[] Adam7 =
    {
        new(0, 0, 8, 8), new(4, 0, 8, 8), new(0, 4, 4, 8), new(2, 0, 4, 4),
        new(0, 2, 2, 4), new(1, 0, 2, 2), new(0, 1, 1, 2),
    };

    /// <summary>Total filtered bytes the image's passes occupy, including one filter byte per row.</summary>
    public static long FilteredLength(int width, int height, int bitsPerPixel, bool interlaced)
    {
        long total = 0;
        foreach (Pass pass in interlaced ? Adam7 : Single)
        {
            long passWidth = Extent(width, pass.X, pass.Dx);
            long passHeight = Extent(height, pass.Y, pass.Dy);
            if (passWidth == 0 || passHeight == 0) continue;
            total += passHeight * (1 + RowBytes(passWidth, bitsPerPixel));
        }
        return total;
    }

    public static byte[] Reconstruct(ReadOnlySpan<byte> filtered, int width, int height, int bitsPerPixel, bool interlaced)
    {
        int pixelBytes = Math.Max(1, bitsPerPixel / 8);
        if (!interlaced && bitsPerPixel >= 8)
            return PngFilters.Unfilter(filtered, width * pixelBytes, height, pixelBytes);

        var output = new byte[width * height * pixelBytes];
        int offset = 0;
        foreach (Pass pass in interlaced ? Adam7 : Single)
        {
            int passWidth = Extent(width, pass.X, pass.Dx);
            int passHeight = Extent(height, pass.Y, pass.Dy);
            if (passWidth == 0 || passHeight == 0) continue;
            int rowBytes = (int)RowBytes(passWidth, bitsPerPixel);
            int length = passHeight * (1 + rowBytes);
            byte[] rows = PngFilters.Unfilter(filtered.Slice(offset, length), rowBytes, passHeight, pixelBytes);
            offset += length;
            for (int py = 0; py < passHeight; py++)
            {
                int y = pass.Y + py * pass.Dy;
                for (int px = 0; px < passWidth; px++)
                {
                    int target = (y * width + pass.X + px * pass.Dx) * pixelBytes;
                    if (bitsPerPixel >= 8)
                    {
                        rows.AsSpan(py * rowBytes + px * pixelBytes, pixelBytes).CopyTo(output.AsSpan(target));
                        continue;
                    }
                    int bit = px * bitsPerPixel;
                    int shift = 8 - bitsPerPixel - bit % 8;
                    output[target] = (byte)((rows[py * rowBytes + bit / 8] >> shift) & ((1 << bitsPerPixel) - 1));
                }
            }
        }
        return output;
    }

    private static int Extent(int size, int origin, int step) =>
        size <= origin ? 0 : (int)(((long)size - origin + step - 1) / step);

    private static long RowBytes(long pixels, int bitsPerPixel) => (pixels * bitsPerPixel + 7) / 8;
}
