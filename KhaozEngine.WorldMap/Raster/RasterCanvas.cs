using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap.Raster;

/// <summary>An opaque float RGB working canvas, quantized only after all painting is complete.</summary>
internal sealed class RasterCanvas
{
    readonly Vector3[] _rgb;

    internal RasterCanvas(int size, MapRect rect, float effectivePixelsPerMetre)
    {
        Size = size;
        Rect = rect;
        EffectivePixelsPerMetre = effectivePixelsPerMetre;
        _rgb = new Vector3[checked(size * size)];
    }

    internal int Size { get; }
    internal MapRect Rect { get; }
    internal float EffectivePixelsPerMetre { get; }

    /// <summary>Converts a raster coordinate to its map-space pixel centre, including coordinates outside the canvas.</summary>
    internal Vector2 MapPoint(float px, float py) =>
        new(Rect.X + (px + 0.5f) / EffectivePixelsPerMetre, Rect.Top - (py + 0.5f) / EffectivePixelsPerMetre);

    internal void Set(int x, int y, Color colour) => _rgb[y * Size + x] = new Vector3(colour.R, colour.G, colour.B);

    /// <summary>Composites colour alpha times coverage over the working RGB, clipping pixels outside the canvas.</summary>
    internal void Blend(int x, int y, Color colour, float coverage)
    {
        if (x < 0 || x >= Size || y < 0 || y >= Size) return;
        int index = y * Size + x;
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

    static byte Channel(float value) => (byte)MathF.Round(MathUtil.Clamp01(value) * 255f);
}
