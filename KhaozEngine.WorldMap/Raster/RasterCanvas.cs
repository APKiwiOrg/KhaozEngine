using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap.Raster;

/// <summary>An opaque float RGB working canvas, quantized only after all painting is complete.</summary>
/// <remarks>
/// Pixel coordinates stay relative to the chunk interior, so gutter pixels use negative coordinates and
/// coordinates from <see cref="Size"/> onward. Interior pixel centres are identical with or without a gutter.
/// </remarks>
internal sealed class RasterCanvas
{
    readonly Vector3[] _rgb;

    internal RasterCanvas(int size, int border, MapRect rect, float effectivePixelsPerMetre)
    {
        Size = size;
        Border = border;
        TextureSize = checked(size + 2 * border);
        Rect = rect;
        PaintedRect = rect.Expand(border / effectivePixelsPerMetre);
        EffectivePixelsPerMetre = effectivePixelsPerMetre;
        _rgb = new Vector3[checked(TextureSize * TextureSize)];
    }

    /// <summary>The interior edge length covering <see cref="Rect"/>.</summary>
    internal int Size { get; }
    internal int Border { get; }
    internal int TextureSize { get; }

    /// <summary>The first painted pixel coordinate on each axis.</summary>
    internal int First => -Border;

    /// <summary>The excluded last painted pixel coordinate on each axis.</summary>
    internal int End => Size + Border;

    /// <summary>The chunk rectangle covered by the interior.</summary>
    internal MapRect Rect { get; }

    /// <summary>The map area covered by the interior and gutter together.</summary>
    internal MapRect PaintedRect { get; }

    internal float EffectivePixelsPerMetre { get; }

    /// <summary>Converts a raster coordinate to its map-space pixel centre, including coordinates outside the canvas.</summary>
    internal Vector2 MapPoint(float px, float py) =>
        new(Rect.X + (px + 0.5f) / EffectivePixelsPerMetre, Rect.Top - (py + 0.5f) / EffectivePixelsPerMetre);

    internal void Set(int x, int y, Color colour) => _rgb[Index(x, y)] = new Vector3(colour.R, colour.G, colour.B);

    /// <summary>Composites colour alpha times coverage over the working RGB, clipping pixels outside the canvas.</summary>
    internal void Blend(int x, int y, Color colour, float coverage)
    {
        if (x < First || x >= End || y < First || y >= End) return;
        int index = Index(x, y);
        float alpha = MathUtil.Clamp01(coverage) * MathUtil.Clamp01(colour.A);
        _rgb[index] = Vector3.Lerp(_rgb[index], new Vector3(colour.R, colour.G, colour.B), alpha);
    }

    internal byte[] ToBytes()
    {
        var bytes = new byte[checked(_rgb.Length * 4)];
        for (int i = 0; i < _rgb.Length; i++)
        {
            bytes[i * 4] = Channel(_rgb[i].X);
            bytes[i * 4 + 1] = Channel(_rgb[i].Y);
            bytes[i * 4 + 2] = Channel(_rgb[i].Z);
            bytes[i * 4 + 3] = 255;
        }
        return bytes;
    }

    int Index(int x, int y) => (y + Border) * TextureSize + x + Border;

    static byte Channel(float value) => (byte)MathF.Round(MathUtil.Clamp01(value) * 255f);
}
