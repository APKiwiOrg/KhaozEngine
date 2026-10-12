using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap.Gui;

/// <summary>A clipped chunk quad. A null texture draws a placeholder.</summary>
internal readonly record struct WorldMapDrawItem<TTexture>(TTexture? Texture, Rect Destination, Vector4 SourceUv);

/// <summary>Clips chunk geometry and matching north-up texture coordinates without changing batch state.</summary>
internal static class WorldMapDrawList
{
    internal static bool TryClip<TTexture>(TTexture? texture, Rect destination, Rect viewport,
        out WorldMapDrawItem<TTexture> item) where TTexture : class
    {
        float left = MathF.Max(destination.X, viewport.X);
        float top = MathF.Max(destination.Y, viewport.Y);
        float right = MathF.Min(destination.Right, viewport.Right);
        float bottom = MathF.Min(destination.Bottom, viewport.Bottom);
        if (right <= left || bottom <= top)
        {
            item = default;
            return false;
        }

        var clipped = new Rect(left, top, right - left, bottom - top);
        var uv = new Vector4(
            (left - destination.X) / destination.Width,
            (top - destination.Y) / destination.Height,
            (right - destination.X) / destination.Width,
            (bottom - destination.Y) / destination.Height);
        item = new WorldMapDrawItem<TTexture>(texture, clipped, uv);
        return true;
    }
}
