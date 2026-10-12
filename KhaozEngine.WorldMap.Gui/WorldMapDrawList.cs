using System;
using System.Numerics;
using KhaozEngine.Primitives;

namespace KhaozEngine.WorldMap.Gui;

/// <summary>A clipped chunk quad. A null texture draws a placeholder.</summary>
internal readonly record struct WorldMapDrawItem<TTexture>(TTexture? Texture, Rect Destination, Vector4 SourceUv);

/// <summary>Clips chunk geometry and matching north-up texture coordinates without changing batch state.</summary>
internal static class WorldMapDrawList
{
    internal static bool TryClip<TTexture>(TTexture? texture, Rect destination, Vector4 sourceUv, Rect viewport,
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
        float uvWidth = sourceUv.Z - sourceUv.X;
        float uvHeight = sourceUv.W - sourceUv.Y;
        var uv = new Vector4(
            sourceUv.X + (left - destination.X) / destination.Width * uvWidth,
            sourceUv.Y + (top - destination.Y) / destination.Height * uvHeight,
            sourceUv.X + (right - destination.X) / destination.Width * uvWidth,
            sourceUv.Y + (bottom - destination.Y) / destination.Height * uvHeight);
        item = new WorldMapDrawItem<TTexture>(texture, clipped, uv);
        return true;
    }
}
