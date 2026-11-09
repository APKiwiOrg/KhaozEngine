using System;

namespace KhaozEngine.Render3D;

/// <summary>Renderer-owned RGBA8 mip staging. Samples source texel centres bilinearly, including odd dimensions.</summary>
internal static class SceneUploadMips
{
    internal static byte[] Downsample(byte[] source, int width, int height, int w, int h)
    {
        var result = new byte[checked(w * h * 4)];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float sx = (x + .5f) * width / w - .5f, sy = (y + .5f) * height / h - .5f;
                int x0 = (int)sx, y0 = (int)sy, x1 = Math.Min(width - 1, x0 + 1), y1 = Math.Min(height - 1, y0 + 1);
                float fx = sx - x0, fy = sy - y0;
                for (int c = 0; c < 4; c++)
                {
                    float a = source[(y0 * width + x0) * 4 + c] * (1 - fx) + source[(y0 * width + x1) * 4 + c] * fx;
                    float b = source[(y1 * width + x0) * 4 + c] * (1 - fx) + source[(y1 * width + x1) * 4 + c] * fx;
                    result[(y * w + x) * 4 + c] = (byte)Math.Clamp((int)MathF.Round(a * (1 - fy) + b * fy), 0, 255);
                }
            }
        return result;
    }
}
