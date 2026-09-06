using System;
using System.IO;

namespace KhaozEngine.Imaging;

internal static class PngFilters
{
    public static byte[] Unfilter(ReadOnlySpan<byte> filtered, int stride, int height, int bytesPerPixel)
    {
        var decoded = new byte[stride * height];
        int source = 0;
        for (int y = 0; y < height; y++)
        {
            byte filter = filtered[source++];
            int row = y * stride;
            for (int x = 0; x < stride; x++)
            {
                byte left = x >= bytesPerPixel ? decoded[row + x - bytesPerPixel] : (byte)0;
                byte above = y > 0 ? decoded[row - stride + x] : (byte)0;
                byte upperLeft = y > 0 && x >= bytesPerPixel
                    ? decoded[row - stride + x - bytesPerPixel]
                    : (byte)0;
                int predictor = filter switch
                {
                    0 => 0,
                    1 => left,
                    2 => above,
                    3 => (left + above) / 2,
                    4 => Paeth(left, above, upperLeft),
                    _ => throw new InvalidDataException($"unsupported PNG filter type {filter}"),
                };
                decoded[row + x] = unchecked((byte)(filtered[source++] + predictor));
            }
        }
        return decoded;
    }

    private static byte Paeth(byte left, byte above, byte upperLeft)
    {
        int estimate = left + above - upperLeft;
        int leftDistance = Math.Abs(estimate - left);
        int aboveDistance = Math.Abs(estimate - above);
        int upperLeftDistance = Math.Abs(estimate - upperLeft);
        if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance) return left;
        return aboveDistance <= upperLeftDistance ? above : upperLeft;
    }
}
