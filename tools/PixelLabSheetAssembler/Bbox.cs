using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PixelLabSheetAssembler;

/// <summary>Computes the inclusive bounding box of a frame's opaque pixels (alpha &gt; threshold).</summary>
public static class Bbox
{
    public static (int MinX, int MinY, int MaxX, int MaxY)? OpaqueBounds(Image<Rgba32> img, int alphaThreshold)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                if (img[x, y].A > alphaThreshold)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < 0) return null;
        return (minX, minY, maxX, maxY);
    }
}
